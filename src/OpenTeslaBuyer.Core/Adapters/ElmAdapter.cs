using System.Globalization;
using System.Runtime.CompilerServices;
using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Adapters;

/// <summary>
/// ELM327-compatible OBD adapters, listening passively to the 500 kbit/s vehicle CAN bus.
/// <list type="bullet">
/// <item>OBDLink (STN chip): hardware pass filters for every wanted ID, then one continuous <c>STM</c> monitor.</item>
/// <item>Plain ELM327 / clones: their buffers overflow on a busy Tesla bus, so they monitor one ID at a time
/// (<c>ATCRA</c> + <c>ATMA</c>) and cycle through the list.</item>
/// </list>
/// Silent monitoring (<c>ATCSM1</c>) keeps the adapter from acknowledging frames; nothing is ever sent to the car.
/// </summary>
public sealed class ElmAdapter(Func<ISerialLink> linkFactory) : ICanAdapter
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(3);

    public string Name => "OBDLink / ELM327";

    /// <summary>How long to listen to the whole bus after connecting, to report what is on it.</summary>
    public TimeSpan BusCheckDuration { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>Plain ELM327 only: the longest wait for one ID before moving on to the next.</summary>
    public TimeSpan PollWindow { get; init; } = TimeSpan.FromSeconds(3);

    public async IAsyncEnumerable<CanFrame> StreamAsync(Func<IReadOnlyList<MonitoredId>> ids, Action<string> log, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var link = linkFactory();
        log($"Opening {link.Description}…");
        await link.OpenAsync(ct);
        var session = new Session(link, log);

        try
        {
            await session.InitialiseAsync(ct);
            var isStn = await session.DetectStnAsync(ct);

            var busCheckIds = new HashSet<uint>();
            await foreach (var frame in session.MonitorAsync(isStn ? "STMA" : "ATMA", BusCheckDuration, int.MaxValue, ct))
            {
                busCheckIds.Add(frame.Id);
                yield return frame;
            }

            var wanted = ids().Select(i => i.Id).ToHashSet();
            log(busCheckIds.Count == 0
                ? "Bus check: no frames. Is the car awake (screen on) and the cable on the vehicle CAN bus?"
                : $"Bus check: {busCheckIds.Count} message IDs seen, {busCheckIds.Count(wanted.Contains)} of {wanted.Count} battery IDs among them.");
            if (busCheckIds.Count > 0 && !busCheckIds.Overlaps(wanted))
                log("None of the battery messages are on this bus. The cable may be wired to chassis CAN instead of vehicle CAN.");

            if (isStn)
            {
                await session.SetPassFiltersAsync(ids(), ct);
                log("Monitoring with hardware filters (STM).");
                await foreach (var frame in session.MonitorAsync("STM", null, int.MaxValue, ct))
                    yield return frame;
            }
            else
            {
                log("Plain ELM327: cycling through the battery IDs one at a time.");
                for (var pass = 0; !ct.IsCancellationRequested; pass++)
                {
                    foreach (var id in ids().Where(i => pass % i.PollEvery == 0))
                    {
                        await session.CommandAsync($"ATCRA {id.Id:X3}", ct, required: true);
                        await foreach (var frame in session.MonitorAsync("ATMA", PollWindow, id.FramesPerVisit, ct))
                            yield return frame;
                    }
                }
            }
        }
        finally
        {
            await session.TryStopAsync();
        }
    }

    /// <summary>Parses a monitor line printed with headers on and DLC display off, e.g. <c>352 0B A1 7F 00 22 33 44 55</c>.</summary>
    public static bool TryParseMonitorLine(string line, DateTimeOffset timestamp, out CanFrame frame)
    {
        frame = null!;
        var hex = line.Replace(" ", "", StringComparison.Ordinal);

        // 11-bit ID (3 hex digits) followed by whole bytes: an odd length between 3 and 19.
        if (hex.Length < 3 || hex.Length > 19 || hex.Length % 2 == 0 || !hex.All(Uri.IsHexDigit))
            return false;

        frame = new CanFrame(
            uint.Parse(hex.AsSpan(0, 3), NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            Convert.FromHexString(hex.AsSpan(3)),
            timestamp);
        return true;
    }

    private sealed class Session(ISerialLink link, Action<string> log)
    {
        // A bare carriage return makes an idle ELM327 repeat its last command (which could restart a monitor),
        // so monitors are stopped with a space instead: it stops a running monitor and is ignored inside commands.
        private const string StopCharacter = " ";

        private readonly LineReader _reader = new(link);
        private bool _monitoring;
        private bool _reportedOverflow;

        public async Task InitialiseAsync(CancellationToken ct)
        {
            // Stop a monitor a previous session may have left running, then drop whatever it printed.
            await link.WriteAsync(StopCharacter, ct);
            await Task.Delay(500, ct);
            _reader.Reset();

            var identity = await CommandAsync("ATZ", ct, required: true, timeout: TimeSpan.FromSeconds(5));
            log($"Adapter: {string.Join(" ", identity.Where(l => l.Length > 0)).Trim()}");

            foreach (var command in new[] { "ATE0", "ATL0", "ATS1", "ATH1", "ATSP6", "ATCAF0" })
                await CommandAsync(command, ct, required: true);

            // Nice to have; some clones answer "?" to these.
            foreach (var command in new[] { "ATCSM1", "ATD0", "ATAL" })
                await CommandAsync(command, ct, required: false);
        }

        public async Task<bool> DetectStnAsync(CancellationToken ct)
        {
            var reply = await CommandAsync("STI", ct, required: false);
            var stn = reply.FirstOrDefault(l => l.StartsWith("STN", StringComparison.OrdinalIgnoreCase));
            log(stn is null ? "No STN chip detected (plain ELM327 command set)." : $"STN chip: {stn}");
            return stn is not null;
        }

        public async Task SetPassFiltersAsync(IReadOnlyList<MonitoredId> ids, CancellationToken ct)
        {
            // Current OBDLink firmware uses STFAC/STFPA; older firmware only knows the deprecated STFCA/STFAP.
            if (IsRejected(await CommandAsync("STFAC", ct, required: false)))
                await CommandAsync("STFCA", ct, required: true);

            for (var i = 0; i < ids.Count; i++)
            {
                var reply = await CommandAsync($"STFPA {ids[i].Id:X3},7FF", ct, required: false);
                if (IsRejected(reply) && !IsOutOfMemory(reply))
                    reply = await CommandAsync($"STFAP {ids[i].Id:X3},7FF", ct, required: true);

                if (IsOutOfMemory(reply))
                {
                    log($"Adapter filter memory is full after {i} of {ids.Count} messages; the remaining lower-priority alert messages will not be received.");
                    return;
                }
            }
        }

        public async Task<List<string>> CommandAsync(string command, CancellationToken ct, bool required, TimeSpan? timeout = null)
        {
            await link.WriteAsync(command + "\r", ct);
            var lines = new List<string>();
            while (true)
            {
                var line = await _reader.ReadLineAsync(timeout ?? CommandTimeout, ct);
                if (line is null)
                    throw new TimeoutException($"No reply to {command}. Check the COM port, baud rate and that the adapter has power.");
                if (line == LineReader.Prompt)
                    break;
                if (line.Length > 0 && !line.Equals(command, StringComparison.OrdinalIgnoreCase))
                    lines.Add(line);
            }

            if (required && IsRejected(lines))
                throw new InvalidOperationException($"Adapter rejected {command}: {string.Join(" ", lines)}");
            return lines;
        }

        public async IAsyncEnumerable<CanFrame> MonitorAsync(string command, TimeSpan? duration, int maxFrames, [EnumeratorCancellation] CancellationToken ct)
        {
            await link.WriteAsync(command + "\r", ct);
            _monitoring = true;
            var deadline = duration is null ? DateTimeOffset.MaxValue : DateTimeOffset.UtcNow + duration.Value;
            var frames = 0;
            var stoppedByAdapter = false;

            while (frames < maxFrames)
            {
                var remaining = deadline - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero)
                    break;

                var line = await _reader.ReadLineAsync(remaining < CommandTimeout ? remaining : CommandTimeout, ct);
                if (line is null)
                {
                    if (duration is null)
                        continue; // quiet bus; keep listening
                    break;
                }

                if (line == LineReader.Prompt)
                {
                    stoppedByAdapter = true; // e.g. after BUFFER FULL the ELM stops monitoring by itself
                    _monitoring = false;
                    break;
                }

                if (TryParseMonitorLine(line, DateTimeOffset.UtcNow, out var frame))
                {
                    frames++;
                    yield return frame;
                }
                else
                {
                    ReportStatusLine(line);
                }
            }

            if (!stoppedByAdapter)
                await StopMonitorAsync(ct);
        }

        public async Task TryStopAsync()
        {
            if (!_monitoring)
                return;

            try
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await StopMonitorAsync(timeout.Token);
            }
            catch (Exception)
            {
                // Best effort: the port may already be gone.
            }
        }

        private async Task StopMonitorAsync(CancellationToken ct)
        {
            // Any character stops ATMA/STM; the adapter then finishes the current line and prints a prompt.
            // If the adapter had already stopped by itself, its prompt is still queued and ends this loop.
            for (var attempt = 0; attempt < 3; attempt++)
            {
                await link.WriteAsync(StopCharacter, ct);
                var until = DateTimeOffset.UtcNow + TimeSpan.FromSeconds(1);
                while (DateTimeOffset.UtcNow < until)
                {
                    var line = await _reader.ReadLineAsync(TimeSpan.FromMilliseconds(500), ct);
                    if (line == LineReader.Prompt)
                    {
                        _monitoring = false;
                        return;
                    }

                    if (line is null)
                        break;
                }
            }

            throw new TimeoutException("Adapter did not stop monitoring.");
        }

        private void ReportStatusLine(string line)
        {
            if (line.Length == 0 || line.StartsWith("STOPPED", StringComparison.OrdinalIgnoreCase) || line.StartsWith("SEARCHING", StringComparison.OrdinalIgnoreCase))
                return;

            if (line.Contains("BUFFER FULL", StringComparison.OrdinalIgnoreCase))
            {
                if (!_reportedOverflow)
                    log("Adapter buffer overflowed (expected with plain ELM327 on a busy bus); continuing.");
                _reportedOverflow = true;
                return;
            }

            if (line.Contains("CAN ERROR", StringComparison.OrdinalIgnoreCase))
            {
                log("CAN ERROR: no valid traffic. Wake the car (open a door, screen on) and check the cable.");
                return;
            }

            if (!line.StartsWith('<'))
                log($"Adapter: {line}");
        }

        private static bool IsRejected(List<string> reply) => reply.Any(l => l == "?" || l.Contains("ERROR", StringComparison.OrdinalIgnoreCase));

        private static bool IsOutOfMemory(List<string> reply) => reply.Any(l => l.Contains("OUT OF MEMORY", StringComparison.OrdinalIgnoreCase));
    }
}

using System.Globalization;
using System.Runtime.CompilerServices;
using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Adapters;

/// <summary>
/// USB-CAN adapters running SLCAN ("Lawicel") firmware, such as CANable with slcan firmware.
/// They pass every frame through, so no ID filtering happens here; the bus is opened listen-only when supported.
/// </summary>
public sealed class SlcanAdapter(Func<ISerialLink> linkFactory) : ICanAdapter
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(2);

    public string Name => "CANable / SLCAN";

    public async IAsyncEnumerable<CanFrame> StreamAsync(Func<IReadOnlyList<MonitoredId>> ids, Action<string> log, [EnumeratorCancellation] CancellationToken ct)
    {
        await using var link = linkFactory();
        log($"Opening {link.Description}…");
        await link.OpenAsync(ct);
        var reader = new LineReader(link);

        // Flush any half-typed command, then make sure the channel is closed before configuring it.
        await link.WriteAsync("\r\r\r", ct);
        await Task.Delay(200, ct);
        reader.Reset();
        await CommandAsync(link, reader, "C", ct);

        if (!await CommandAsync(link, reader, "S6", ct))
            throw new InvalidOperationException("Adapter rejected the 500 kbit/s bitrate (S6). Is this an SLCAN adapter?");
        await CommandAsync(link, reader, "Z0", ct); // timestamps off; optional

        if (await CommandAsync(link, reader, "L", ct))
        {
            log("CAN channel open in listen-only mode at 500 kbit/s.");
        }
        else if (await CommandAsync(link, reader, "O", ct))
        {
            log("Firmware has no listen-only mode; channel open normally at 500 kbit/s (the adapter will acknowledge frames but never sends any).");
        }
        else
        {
            throw new InvalidOperationException("Adapter refused to open the CAN channel.");
        }

        try
        {
            while (true)
            {
                var line = await reader.ReadLineAsync(ct);
                if (line is null)
                    yield break;
                if (TryParseFrame(line, DateTimeOffset.UtcNow, out var frame))
                    yield return frame;
            }
        }
        finally
        {
            try
            {
                await link.WriteAsync("C\r", CancellationToken.None);
            }
            catch (Exception)
            {
                // Best effort: the adapter may already be unplugged.
            }
        }
    }

    /// <summary>Parses <c>tIIILDD…</c> (11-bit) and <c>TIIIIIIIILDD…</c> (29-bit) frames; any trailing timestamp is ignored.</summary>
    public static bool TryParseFrame(string line, DateTimeOffset timestamp, out CanFrame frame)
    {
        frame = null!;
        if (line.Length < 5 || (line[0] != 't' && line[0] != 'T'))
            return false;

        var extended = line[0] == 'T';
        var idLength = extended ? 8 : 3;
        if (line.Length < 1 + idLength + 1
            || !uint.TryParse(line.AsSpan(1, idLength), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var id)
            || !int.TryParse(line.AsSpan(1 + idLength, 1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var length)
            || length > 8)
        {
            return false;
        }

        var dataStart = 2 + idLength;
        if (line.Length < dataStart + length * 2)
            return false;

        try
        {
            frame = new CanFrame(id, Convert.FromHexString(line.AsSpan(dataStart, length * 2)), timestamp, extended);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    /// <summary>Sends a command and waits for the acknowledgement: CR for success, BEL for failure.</summary>
    private static async Task<bool> CommandAsync(ISerialLink link, LineReader reader, string command, CancellationToken ct)
    {
        await link.WriteAsync(command + "\r", ct);
        while (true)
        {
            var reply = await reader.ReadLineAsync(CommandTimeout, ct);
            if (reply is null)
                throw new TimeoutException($"No reply to SLCAN command {command}. Check the COM port and that the adapter runs slcan firmware.");
            if (reply == LineReader.Bell)
                return false;
            if (reply.Length == 0)
                return true;
            // Anything else is a frame or noise that arrived before the reply; skip it.
        }
    }
}

using System.Runtime.CompilerServices;
using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Adapters;

/// <summary>Plays back a candump log, keeping the original timing (scaled by <paramref name="speed"/>).</summary>
public sealed class ReplayAdapter(string path, double speed = 1.0) : ICanAdapter
{
    public string Name => "Replay log file";

    public async IAsyncEnumerable<CanFrame> StreamAsync(Func<IReadOnlyList<MonitoredId>> ids, Action<string> log, [EnumeratorCancellation] CancellationToken ct)
    {
        log($"Replaying {Path.GetFileName(path)}…");
        using var reader = new StreamReader(path);
        var started = DateTimeOffset.UtcNow;
        DateTimeOffset? first = null;
        long frames = 0, skipped = 0;

        while (await reader.ReadLineAsync(ct) is { } line)
        {
            if (line.Length == 0 || line.StartsWith('#'))
                continue;

            if (!CandumpLog.TryParse(line, out var frame))
            {
                skipped++;
                continue;
            }

            first ??= frame.Timestamp;
            if (speed > 0)
            {
                var due = started + (frame.Timestamp - first.Value) / speed;
                var wait = due - DateTimeOffset.UtcNow;
                if (wait > TimeSpan.FromMilliseconds(5))
                    await Task.Delay(wait, ct);
            }

            frames++;
            yield return frame;
        }

        log(skipped == 0
            ? $"Replay finished: {frames} frames."
            : $"Replay finished: {frames} frames, {skipped} unreadable lines skipped.");
    }
}

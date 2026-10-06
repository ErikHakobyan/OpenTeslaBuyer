using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace OpenTeslaBuyer.Core.Can;

/// <summary>
/// Reads and writes Linux <c>candump -L</c> lines, e.g. <c>(1696000000.123456) can0 352#0011223344556677</c>.
/// The format is plain text, widely supported (SavvyCAN, python-can, cantools) and easy to diff.
/// </summary>
public static partial class CandumpLog
{
    [GeneratedRegex(@"^\((?<sec>\d+)\.(?<frac>\d+)\)\s+\S+\s+(?<id>[0-9A-Fa-f]{3}|[0-9A-Fa-f]{8})#(?<data>[0-9A-Fa-f]*)\s*$")]
    private static partial Regex LinePattern();

    public static string FormatId(CanFrame frame) =>
        frame.Extended ? frame.Id.ToString("X8") : frame.Id.ToString("X3");

    public static string Format(CanFrame frame, string channel = "can0")
    {
        var ticks = (frame.Timestamp - DateTimeOffset.UnixEpoch).Ticks;
        var seconds = ticks / TimeSpan.TicksPerSecond;
        var micros = ticks % TimeSpan.TicksPerSecond / 10;
        return $"({seconds}.{micros:D6}) {channel} {FormatId(frame)}#{Convert.ToHexString(frame.Data)}";
    }

    public static bool TryParse(string line, out CanFrame frame)
    {
        frame = null!;
        var match = LinePattern().Match(line);
        if (!match.Success || match.Groups["data"].Length % 2 != 0 || match.Groups["data"].Length > 16)
            return false;

        var fraction = match.Groups["frac"].Value.PadRight(7, '0')[..7];
        var ticks = long.Parse(match.Groups["sec"].Value, CultureInfo.InvariantCulture) * TimeSpan.TicksPerSecond
                    + long.Parse(fraction, CultureInfo.InvariantCulture);
        var id = match.Groups["id"].Value;

        frame = new CanFrame(
            uint.Parse(id, NumberStyles.HexNumber, CultureInfo.InvariantCulture),
            Convert.FromHexString(match.Groups["data"].Value),
            DateTimeOffset.UnixEpoch.AddTicks(ticks),
            Extended: id.Length == 8);
        return true;
    }
}

/// <summary>Appends frames to a candump log. Safe to call from the adapter's background thread.</summary>
public sealed class CandumpWriter : IDisposable
{
    private readonly StreamWriter _writer;
    private readonly object _gate = new();
    private bool _disposed;

    public CandumpWriter(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
        _writer = new StreamWriter(path, append: false, Encoding.ASCII);
    }

    public string Path { get; }

    public long FrameCount { get; private set; }

    /// <summary>Appends a frame; frames arriving after <see cref="Dispose"/> (from a racing reader thread) are dropped.</summary>
    public void Write(CanFrame frame)
    {
        lock (_gate)
        {
            if (_disposed)
                return;

            _writer.WriteLine(CandumpLog.Format(frame));
            FrameCount++;
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _writer.Dispose();
        }
    }
}

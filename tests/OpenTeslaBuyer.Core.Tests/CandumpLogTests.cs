using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Tests;

public class CandumpLogTests
{
    [Fact]
    public void Formats_and_parses_standard_frame()
    {
        var frame = new CanFrame(0x352, [0x00, 0x00, 0x7C, 0x0D, 0, 0, 0, 0], DateTimeOffset.FromUnixTimeMilliseconds(1_700_000_000_123).AddTicks(4560));

        var line = CandumpLog.Format(frame);

        Assert.Equal("(1700000000.123456) can0 352#00007C0D00000000", line);
        Assert.True(CandumpLog.TryParse(line, out var parsed));
        Assert.Equal(frame.Id, parsed.Id);
        Assert.Equal(frame.Data, parsed.Data);
        Assert.Equal(frame.Timestamp, parsed.Timestamp);
        Assert.False(parsed.Extended);
    }

    [Fact]
    public void Parses_extended_frame_and_short_fraction()
    {
        Assert.True(CandumpLog.TryParse("(12.5) vcan0 18DAF110#0102", out var frame));

        Assert.True(frame.Extended);
        Assert.Equal(0x18DAF110u, frame.Id);
        Assert.Equal(new byte[] { 1, 2 }, frame.Data);
        Assert.Equal(DateTimeOffset.UnixEpoch.AddSeconds(12.5), frame.Timestamp);
    }

    [Theory]
    [InlineData("")]
    [InlineData("# comment")]
    [InlineData("(1.0) can0 352#123")]
    [InlineData("(1.0) can0 352##0123")]
    [InlineData("can0 352 [8] 00 11 22 33 44 55 66 77")]
    public void Rejects_other_lines(string line)
    {
        Assert.False(CandumpLog.TryParse(line, out _));
    }
}

using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Tests;

public class SignalTests
{
    [Fact]
    public void Decodes_little_endian_signal_crossing_byte_boundary()
    {
        // 11 bits starting at bit 0: 0x2CC = 716 -> 71.6 kWh at 0.1 kWh per bit.
        var data = new byte[] { 0xCC, 0x02, 0, 0, 0, 0, 0, 0 };

        Assert.Equal(71.6, new Signal(0, 11, 0.1).Decode(data)!.Value, 6);
    }

    [Fact]
    public void Encode_then_decode_round_trips()
    {
        var signal = new Signal(16, 16, 0.02);
        var data = new byte[8];

        signal.Encode(data, 71.6);

        Assert.Equal(71.6, signal.Decode(data)!.Value, 6);
    }

    [Fact]
    public void Encoding_one_signal_leaves_neighbouring_bits_alone()
    {
        var first = new Signal(0, 11, 0.1);
        var second = new Signal(11, 11, 0.1);
        var data = new byte[8];

        first.Encode(data, 75.0);
        second.Encode(data, 40.0);

        Assert.Equal(75.0, first.Decode(data)!.Value, 6);
        Assert.Equal(40.0, second.Decode(data)!.Value, 6);
    }

    [Fact]
    public void Signed_signal_sign_extends()
    {
        var current = new Signal(16, 15, -0.1, Signed: true);
        var data = new byte[8];

        current.Encode(data, 12.3);

        Assert.Equal(12.3, current.Decode(data)!.Value, 6);
        Assert.Equal(-123L & 0x7FFF, (long)current.Raw(data));
    }

    [Fact]
    public void All_ones_unsigned_value_is_not_available()
    {
        var data = new byte[] { 0xFF, 0x07, 0, 0, 0, 0, 0, 0 };

        Assert.Null(new Signal(0, 11, 0.1).Decode(data));
    }

    [Fact]
    public void Signal_beyond_payload_is_not_available()
    {
        Assert.Null(new Signal(32, 32).Decode(new byte[4]));
    }
}

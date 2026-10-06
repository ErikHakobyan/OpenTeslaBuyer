namespace OpenTeslaBuyer.Core.Can;

/// <summary>
/// A little-endian ("Intel", <c>@1</c> in DBC notation) signal inside a CAN payload.
/// Every Tesla signal this tool reads uses that byte order, so big-endian is not supported.
/// </summary>
public sealed record Signal(int StartBit, int Length, double Factor = 1, double Offset = 0, bool Signed = false)
{
    private ulong Mask => Length == 64 ? ulong.MaxValue : (1UL << Length) - 1;

    public bool Fits(ReadOnlySpan<byte> data) => StartBit + Length <= Math.Min(data.Length, 8) * 8;

    public ulong Raw(ReadOnlySpan<byte> data) => (ReadWord(data) >> StartBit) & Mask;

    /// <summary>
    /// The physical value, or null when the payload is too short or an unsigned signal holds all ones,
    /// which Tesla uses as "SNA" (signal not available).
    /// </summary>
    public double? Decode(ReadOnlySpan<byte> data)
    {
        if (!Fits(data))
            return null;

        var raw = Raw(data);
        if (!Signed && Length > 1 && raw == Mask)
            return null;

        var value = Signed && (raw & (1UL << (Length - 1))) != 0
            ? (long)(raw | ~Mask)
            : (long)raw;
        return value * Factor + Offset;
    }

    public void Encode(Span<byte> data, double value)
    {
        var raw = (long)Math.Round((value - Offset) / Factor);
        EncodeRaw(data, (ulong)raw);
    }

    public void EncodeRaw(Span<byte> data, ulong raw)
    {
        var word = ReadWord(data);
        word &= ~(Mask << StartBit);
        word |= (raw & Mask) << StartBit;
        for (var i = 0; i < Math.Min(data.Length, 8); i++)
            data[i] = (byte)(word >> (8 * i));
    }

    private static ulong ReadWord(ReadOnlySpan<byte> data)
    {
        ulong word = 0;
        for (var i = Math.Min(data.Length, 8) - 1; i >= 0; i--)
            word = (word << 8) | data[i];
        return word;
    }
}

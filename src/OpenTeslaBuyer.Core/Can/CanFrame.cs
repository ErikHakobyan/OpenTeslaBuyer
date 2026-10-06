namespace OpenTeslaBuyer.Core.Can;

/// <summary>A classic CAN frame (up to 8 data bytes) as received from an adapter or read from a log.</summary>
public sealed record CanFrame(uint Id, byte[] Data, DateTimeOffset Timestamp, bool Extended = false)
{
    public override string ToString() => $"{CandumpLog.FormatId(this)}#{Convert.ToHexString(Data)}";
}

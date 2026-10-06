using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Vehicles;

/// <summary>
/// The BMS energy message: 0x352 on the Model 3 platform, 0x382 on 2012–2021 Model S/X. Tesla changed its layout
/// with firmware updates; three are known:
/// <list type="bullet">
/// <item>10-bit fields: early firmware (wk057's Model S notes; early Model 3 DBCs).</item>
/// <item>11-bit fields: joshwardell/model3dbc (2020 firmware). Needed once packs exceeded 102.3 kWh nominal.</item>
/// <item>Multiplexed pages with 16-bit fields: newer Model 3/Y firmware (onyx-m2-dbc).</item>
/// </list>
/// </summary>
public static class EnergyStatus
{
    public static class Multiplexed
    {
        public static readonly Signal Page = new(0, 2);

        // Page 0
        public static readonly Signal NominalFullPack = new(16, 16, 0.02);
        public static readonly Signal NominalRemaining = new(32, 16, 0.02);
        public static readonly Signal IdealRemaining = new(48, 16, 0.02);

        // Page 1
        public static readonly Signal FullyCharged = new(15, 1);
        public static readonly Signal EnergyBuffer = new(16, 16, 0.01);
        public static readonly Signal ExpectedRemaining = new(32, 16, 0.02);
        public static readonly Signal EnergyToChargeComplete = new(48, 16, 0.02);
    }

    public static class Bits11
    {
        public static readonly Signal NominalFullPack = new(0, 11, 0.1);
        public static readonly Signal NominalRemaining = new(11, 11, 0.1);
        public static readonly Signal ExpectedRemaining = new(22, 11, 0.1);
        public static readonly Signal IdealRemaining = new(33, 11, 0.1);
        public static readonly Signal EnergyToChargeComplete = new(44, 11, 0.1);
        public static readonly Signal EnergyBuffer = new(55, 8, 0.1);
        public static readonly Signal FullyCharged = new(63, 1);
    }

    public static class Bits10
    {
        public static readonly Signal NominalFullPack = new(0, 10, 0.1);
        public static readonly Signal NominalRemaining = new(10, 10, 0.1);
        public static readonly Signal ExpectedRemaining = new(20, 10, 0.1);
        public static readonly Signal IdealRemaining = new(30, 10, 0.1);
        public static readonly Signal EnergyToChargeComplete = new(40, 10, 0.1);
        public static readonly Signal EnergyBuffer = new(50, 8, 0.1);
    }

    public static void Decode(EnergyLayout layout, byte[] d, BatteryData data)
    {
        switch (layout)
        {
            case EnergyLayout.Multiplexed when Multiplexed.Page.Raw(d) == 0:
                data.NominalFullPackKWh = Multiplexed.NominalFullPack.Decode(d) ?? data.NominalFullPackKWh;
                data.NominalRemainingKWh = Multiplexed.NominalRemaining.Decode(d) ?? data.NominalRemainingKWh;
                data.IdealRemainingKWh = Multiplexed.IdealRemaining.Decode(d) ?? data.IdealRemainingKWh;
                break;

            case EnergyLayout.Multiplexed when Multiplexed.Page.Raw(d) == 1:
                data.FullyCharged = Multiplexed.FullyCharged.Raw(d) == 1;
                data.EnergyBufferKWh = Multiplexed.EnergyBuffer.Decode(d) ?? data.EnergyBufferKWh;
                data.ExpectedRemainingKWh = Multiplexed.ExpectedRemaining.Decode(d) ?? data.ExpectedRemainingKWh;
                data.EnergyToChargeCompleteKWh = Multiplexed.EnergyToChargeComplete.Decode(d) ?? data.EnergyToChargeCompleteKWh;
                break;

            case EnergyLayout.Bits11:
                data.NominalFullPackKWh = Bits11.NominalFullPack.Decode(d) ?? data.NominalFullPackKWh;
                data.NominalRemainingKWh = Bits11.NominalRemaining.Decode(d) ?? data.NominalRemainingKWh;
                data.ExpectedRemainingKWh = Bits11.ExpectedRemaining.Decode(d) ?? data.ExpectedRemainingKWh;
                data.IdealRemainingKWh = Bits11.IdealRemaining.Decode(d) ?? data.IdealRemainingKWh;
                data.EnergyToChargeCompleteKWh = Bits11.EnergyToChargeComplete.Decode(d) ?? data.EnergyToChargeCompleteKWh;
                data.EnergyBufferKWh = Bits11.EnergyBuffer.Decode(d) ?? data.EnergyBufferKWh;
                data.FullyCharged = Bits11.FullyCharged.Raw(d) == 1;
                break;

            case EnergyLayout.Bits10:
                data.NominalFullPackKWh = Bits10.NominalFullPack.Decode(d) ?? data.NominalFullPackKWh;
                data.NominalRemainingKWh = Bits10.NominalRemaining.Decode(d) ?? data.NominalRemainingKWh;
                data.ExpectedRemainingKWh = Bits10.ExpectedRemaining.Decode(d) ?? data.ExpectedRemainingKWh;
                data.IdealRemainingKWh = Bits10.IdealRemaining.Decode(d) ?? data.IdealRemainingKWh;
                data.EnergyToChargeCompleteKWh = Bits10.EnergyToChargeComplete.Decode(d) ?? data.EnergyToChargeCompleteKWh;
                data.EnergyBufferKWh = Bits10.EnergyBuffer.Decode(d) ?? data.EnergyBufferKWh;
                break;
        }
    }

    public static string Describe(EnergyLayout layout) => layout switch
    {
        EnergyLayout.Multiplexed => "multiplexed",
        EnergyLayout.Bits11 => "11-bit",
        EnergyLayout.Bits10 => "10-bit",
        _ => "detecting…",
    };
}

/// <summary>
/// Works out which energy-message layout the car uses. A layout qualifies when a handful of frames all decode to
/// plausible values with it (and, for the fixed layouts, a steady full-pack capacity). Once the displayed state of
/// charge is known, the layout must also reproduce it: SOC ≈ (remaining − buffer) / (full − buffer). Read with the
/// wrong layout, the fields come out shifted, so remaining energy is roughly doubled or halved and fails that check.
/// </summary>
public sealed class EnergyLayoutDetector(params EnergyLayout[] candidates)
{
    private const int MinSamples = 4;
    private const int MaxSamples = 8;

    private readonly List<byte[]> _samples = [];

    /// <summary>Adds a frame; returns the layout once exactly one candidate qualifies, otherwise <see cref="EnergyLayout.Auto"/>.</summary>
    public EnergyLayout Detect(byte[] frame, double? socUiPercent)
    {
        _samples.Add(frame);
        if (_samples.Count > MaxSamples)
            _samples.RemoveAt(0);
        if (_samples.Count < MinSamples)
            return EnergyLayout.Auto;

        var qualifying = candidates.Where(IsConsistent).ToList();
        if (socUiPercent is { } soc)
        {
            var tolerance = Math.Max(5, soc * 0.25);
            qualifying = qualifying.Where(layout => PredictedSoc(layout) is { } predicted && Math.Abs(predicted - soc) <= tolerance).ToList();
        }

        return qualifying.Count == 1 ? qualifying[0] : EnergyLayout.Auto;
    }

    private bool IsConsistent(EnergyLayout layout)
    {
        if (layout == EnergyLayout.Multiplexed)
            return _samples.Select(s => EnergyStatus.Multiplexed.Page.Raw(s)).Distinct().Count() > 1 && _samples.All(FitsMultiplexed);

        var decoded = _samples.Select(s =>
        {
            var data = new BatteryData();
            EnergyStatus.Decode(layout, s, data);
            return data;
        }).ToList();
        var capacities = decoded.Select(d => d.NominalFullPackKWh ?? 0).ToList();
        return decoded.All(Plausible) && capacities.Max() - capacities.Min() <= 0.5;
    }

    private static bool FitsMultiplexed(byte[] d)
    {
        var data = new BatteryData();
        EnergyStatus.Decode(EnergyLayout.Multiplexed, d, data);
        return EnergyStatus.Multiplexed.Page.Raw(d) switch
        {
            0 => data.NominalFullPackKWh is >= 20 and <= 150 && data.NominalRemainingKWh <= data.NominalFullPackKWh * 1.05,
            1 => data.EnergyBufferKWh is >= 0 and <= 10,
            _ => true, // pages this tool does not decode
        };
    }

    private static bool Plausible(BatteryData d) =>
        d.NominalFullPackKWh is >= 20 and <= 150
        && d.NominalRemainingKWh <= d.NominalFullPackKWh * 1.05
        && d.EnergyBufferKWh is >= 0 and <= 10;

    private double? PredictedSoc(EnergyLayout layout)
    {
        var data = new BatteryData();
        foreach (var sample in _samples)
            EnergyStatus.Decode(layout, sample, data);

        var usable = data.NominalFullPackKWh - (data.EnergyBufferKWh ?? 0);
        return usable is > 0 ? (data.NominalRemainingKWh - (data.EnergyBufferKWh ?? 0)) / usable * 100 : null;
    }
}

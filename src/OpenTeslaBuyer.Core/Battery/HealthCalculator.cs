using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Battery;

public enum HealthRating
{
    Unknown,
    Excellent,
    Good,
    Fair,
    Poor,
}

public enum CapacitySource
{
    Unknown,
    Bms,
    Manual,

    /// <summary>The user chose the pack type; its typical as-new capacity is used.</summary>
    PackChosen,

    /// <summary>Estimated from the likeliest pack type (pre-2021 Model S/X).</summary>
    Estimated,
}

public sealed record HealthReport
{
    public double? OriginalKWh { get; init; }

    public CapacitySource OriginalSource { get; init; }

    /// <summary>The pack the original capacity comes from, when chosen or estimated.</summary>
    public PackType? Pack { get; init; }

    /// <summary>The automatic pack estimate, when one was made (pre-2021 Model S/X).</summary>
    public PackEstimate? PackEstimate { get; init; }

    public double? CurrentKWh { get; init; }

    public double? StateOfHealthPercent { get; init; }

    public double? DegradationPercent { get; init; }

    public double? LostKWh { get; init; }

    /// <summary>Full-charge energy the driver can use: current capacity minus the hidden buffer.</summary>
    public double? UsableKWh { get; init; }

    public double? UsableRemainingKWh { get; init; }

    /// <summary>Rated range at 100% charge, from usable energy and the car's rated consumption.</summary>
    public double? FullRangeKm { get; init; }

    public double? CellSpreadMv { get; init; }

    /// <summary>Lifetime discharged energy divided by the original capacity.</summary>
    public double? EquivalentFullCycles { get; init; }

    public HealthRating Rating { get; init; }

    public IReadOnlyList<string> Notes { get; init; } = [];
}

public static class HealthCalculator
{
    /// <summary>Capacity retention Tesla's battery warranty guarantees for the warranty period.</summary>
    public const double WarrantyRetentionPercent = 70;

    private const double AtRestCurrentAmps = 5;

    /// <param name="manualOriginalKWh">Capacity when new typed in by the user; wins over everything else.</param>
    /// <param name="packKey">A pack type chosen by the user (see <see cref="PackEstimator.Packs"/>).</param>
    public static HealthReport Evaluate(BatteryData data, double? manualOriginalKWh = null, string? packKey = null)
    {
        var notes = new List<string>();
        var estimate = data.Platform == VehiclePlatform.LegacyModelSX && data.InitialFullPackKWh is null
            ? PackEstimator.Estimate(data)
            : null;
        var chosen = packKey is null ? null : PackEstimator.Find(packKey);

        var (original, source, pack) = manualOriginalKWh is > 0
            ? (manualOriginalKWh, CapacitySource.Manual, (PackType?)null)
            : data.InitialFullPackKWh is { } fromBms
                ? (fromBms, CapacitySource.Bms, null)
                : chosen is not null
                    ? (chosen.NewKWh, CapacitySource.PackChosen, chosen)
                    : estimate is not null
                        ? (estimate.Pack.NewKWh, CapacitySource.Estimated, estimate.Pack)
                        : ((double?)null, CapacitySource.Unknown, null);
        var current = data.NominalFullPackKWh;

        double? soh = original is > 0 && current is { } now ? now / original.Value * 100 : null;
        double? usable = current - data.EnergyBufferKWh;
        double? usableRemaining = data.NominalRemainingKWh - data.EnergyBufferKWh is { } r ? Math.Max(0, r) : null;
        double? fullRangeKm = usable * 1000 / data.RatedWhPerKm;
        double? cycles = data.DischargeTotalKWh / (original ?? current);
        var spread = CellSpreadMv(data);

        if (current is not null && source == CapacitySource.Unknown)
        {
            notes.Add(data.Platform == VehiclePlatform.LegacyModelSX
                ? "Model S/X built before 2021 do not report the pack's capacity when new, and the pack could not be estimated yet (the VIN and odometer are needed). Choose the battery pack to calculate degradation."
                : "The pack's original capacity has not been received yet. If it never appears, type it into \"Capacity when new\".");
        }

        if (source == CapacitySource.Estimated && estimate is not null)
        {
            notes.Add($"Original capacity is estimated: {estimate.Pack.Name}, typically {estimate.Pack.NewKWh:0.0} kWh when new (±{estimate.Pack.TolerancePercent:0}%), so health is approximate."
                      + (estimate.Confident ? "" : $" Another pack fits almost as well ({string.Join(", ", estimate.Alternatives.Select(p => p.Name))}); choose the pack if you know it."));
        }

        if (source == CapacitySource.PackChosen && pack is not null)
            notes.Add($"Original capacity from the chosen pack: {pack.Name}, typically {pack.NewKWh:0.0} kWh when new (±{pack.TolerancePercent:0}%).");

        if (source == CapacitySource.Manual)
            notes.Add(data.InitialFullPackKWh is null
                ? $"Original capacity entered manually ({original:0.0} kWh)."
                : $"Original capacity entered manually ({original:0.0} kWh) instead of the value stored in the BMS ({data.InitialFullPackKWh:0.0} kWh).");

        if (soh is { } health)
        {
            if (health > 100)
                notes.Add("Capacity reads above the original value. That is common on newer packs, because the BMS estimate floats.");
            notes.Add("Capacity is the BMS's own estimate. It recalibrates after deep discharges and full charges, so readings can move by a few percent from day to day.");
        }

        if (spread is not null && data.PackCurrent is { } amps && Math.Abs(amps) > AtRestCurrentAmps)
            notes.Add("The pack is under load, so cell spread is not representative and is not checked. Read it with the car parked and not charging.");

        if (data.Vin is { } vin && new VinInfo(vin).Model == TeslaModel.Cybertruck)
            notes.Add("The Cybertruck is not supported. Values may be missing or wrong.");

        return new HealthReport
        {
            OriginalKWh = original,
            OriginalSource = source,
            Pack = pack,
            PackEstimate = estimate,
            CurrentKWh = current,
            StateOfHealthPercent = soh,
            DegradationPercent = soh is { } s ? Math.Max(0, 100 - s) : null,
            LostKWh = original - current is { } lost ? Math.Max(0, lost) : null,
            UsableKWh = usable,
            UsableRemainingKWh = usableRemaining,
            FullRangeKm = fullRangeKm,
            CellSpreadMv = spread,
            EquivalentFullCycles = cycles,
            Rating = Rate(soh),
            Notes = notes,
        };
    }

    public static HealthRating Rate(double? soh) => soh switch
    {
        null => HealthRating.Unknown,
        >= 90 => HealthRating.Excellent,
        >= 80 => HealthRating.Good,
        >= WarrantyRetentionPercent => HealthRating.Fair,
        _ => HealthRating.Poor,
    };

    private static double? CellSpreadMv(BatteryData data)
    {
        if (data.BrickVoltages.Count >= 10)
            return (data.BrickVoltages.Values.Max() - data.BrickVoltages.Values.Min()) * 1000;
        return (data.BrickVoltageMax - data.BrickVoltageMin) * 1000;
    }
}

using System.Globalization;

namespace OpenTeslaBuyer.Core.Battery;

/// <summary>
/// Every cell group's voltage averaged over a stretch of rest with the car parked. Saved, so a later connection can
/// compare it with a fresh one.
/// </summary>
/// <param name="Volts">Volts per cell group, keyed like <see cref="BatteryData.BrickVoltages"/>.</param>
public sealed record CellSnapshot(DateTimeOffset TakenAt, IReadOnlyDictionary<int, double> Volts, double? SocPercent, double? TempMinC, double? TempMaxC)
{
    public double MedianVolts => CellDrift.Median(Volts.Values);
}

/// <summary>How far one cell group fell behind (or moved ahead of) the pack's typical group between two snapshots.</summary>
/// <param name="ChangeMv">Change against the typical group; negative means it lost more charge than the others.</param>
/// <param name="MvPerDay"><paramref name="ChangeMv"/> scaled to 24 hours.</param>
public sealed record GroupDrift(int Index, double ChangeMv, double MvPerDay, GroupFinding Finding);

/// <summary>The overnight test: two parked snapshots of the same car compared.</summary>
public sealed record DriftResult(CellSnapshot Earlier, CellSnapshot Later, IReadOnlyList<GroupDrift> Groups)
{
    /// <summary>Groups that lost noticeably more charge than the rest, worst first.</summary>
    public IReadOnlyList<GroupDrift> Flagged { get; } =
        Groups.Where(g => g.Finding != GroupFinding.Normal).OrderBy(g => g.MvPerDay).ToList();

    public TimeSpan Elapsed => Later.TakenAt - Earlier.TakenAt;

    public GroupFinding Worst => Flagged.Count == 0 ? GroupFinding.Normal : Flagged.Max(g => g.Finding);

    /// <summary>
    /// Iron-phosphate (LFP) packs sit at an almost constant voltage over most of their charge, so a group losing charge
    /// barely shows; on those packs the test only catches large losses.
    /// </summary>
    public bool FlatVoltagePack => Later.MedianVolts < 3.45;

    public string Summary => Flagged.Count switch
    {
        0 => $"All {Groups.Count} cell groups held their charge alike over {Span(Elapsed)}.",
        1 => $"Cell group {Flagged[0].Index + 1} lost more charge than the others over {Span(Elapsed)}.",
        _ => $"{Flagged.Count} cell groups lost more charge than the others over {Span(Elapsed)}: "
             + string.Join(", ", Flagged.Take(6).Select(g => (g.Index + 1).ToString(CultureInfo.CurrentCulture)))
             + (Flagged.Count > 6 ? "…" : "") + ".",
    };

    /// <summary>E.g. "Cell group 21: 9.0 mV below the others (21.6 mV per day)".</summary>
    public static string Describe(GroupDrift group) =>
        $"Cell group {group.Index + 1}: {Math.Abs(group.ChangeMv).ToString("0.0", CultureInfo.CurrentCulture)} mV below the others "
        + $"({Math.Abs(group.MvPerDay).ToString("0.0", CultureInfo.CurrentCulture)} mV per day)";

    public string ToText()
    {
        var lines = new List<string> { $"Overnight test: {Summary}" };
        lines.AddRange(Flagged.Take(10).Select(g => "  " + Describe(g)));
        lines.Add($"  {Earlier.TakenAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)} → {Later.TakenAt.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}");
        return string.Join(Environment.NewLine, lines);
    }

    public static string Span(TimeSpan span) => span.TotalHours >= 48
        ? span.TotalDays.ToString("0", CultureInfo.CurrentCulture) + " days"
        : span.TotalHours.ToString("0", CultureInfo.CurrentCulture) + " hours";
}

/// <summary>
/// Compares two parked snapshots of a pack. Each group is measured against the pack's median group, so the pack's own
/// discharge and temperature changes cancel out; what remains is a group losing charge faster than the others, which
/// is how a cell with an internal leak (high self-discharge) shows itself.
/// </summary>
public static class CellDrift
{
    public static readonly TimeSpan MinimumGap = TimeSpan.FromHours(4);
    public static readonly TimeSpan MaximumGap = TimeSpan.FromDays(30);

    internal const double HigherMvPerDay = 3;
    internal const double MuchHigherMvPerDay = 8;

    /// <summary>Smaller changes are below what the readings can resolve once averaged.</summary>
    internal const double MinimumChangeMv = 2;

    internal const int MinimumGroups = 8;

    /// <summary>The comparison, or null when the snapshots are too close together, too far apart or too different.</summary>
    public static DriftResult? Compare(CellSnapshot earlier, CellSnapshot later)
    {
        var gap = later.TakenAt - earlier.TakenAt;
        if (gap < MinimumGap || gap > MaximumGap)
            return null;

        var groups = earlier.Volts.Keys.Intersect(later.Volts.Keys).Order().ToList();
        if (groups.Count < MinimumGroups)
            return null;

        var medianBefore = Median(groups.Select(g => earlier.Volts[g]));
        var medianAfter = Median(groups.Select(g => later.Volts[g]));
        var changes = groups.Select(g => (
            Index: g,
            ChangeMv: ((later.Volts[g] - medianAfter) - (earlier.Volts[g] - medianBefore)) * 1000,
            BelowNow: later.Volts[g] < medianAfter)).ToList();

        var typical = Median(changes.Select(c => c.ChangeMv));
        var sigma = Math.Max(1.4826 * Median(changes.Select(c => Math.Abs(c.ChangeMv - typical))), 0.2);
        var days = gap.TotalDays;

        var results = changes.Select(c =>
        {
            var change = c.ChangeMv - typical;
            var perDay = change / days;

            // Only a group that ends up below the others counts: the BMS balances by bleeding the highest groups down,
            // which also shows as a drop.
            var dropped = c.BelowNow && -change >= Math.Max(MinimumChangeMv, 5 * sigma);
            var finding = !dropped ? GroupFinding.Normal
                : -perDay >= MuchHigherMvPerDay ? GroupFinding.MuchHigher
                : -perDay >= HigherMvPerDay ? GroupFinding.Higher
                : GroupFinding.Normal;
            return new GroupDrift(c.Index, change, perDay, finding);
        }).ToList();

        return new DriftResult(earlier, later, results);
    }

    internal static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        if (sorted.Length == 0)
            return double.NaN;

        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }
}

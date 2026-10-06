using System.Globalization;
using System.Text.Json.Serialization;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Buyer;

namespace OpenTeslaBuyer.Core.Storage;

/// <summary>A cell group a test flagged, as kept with a saved check.</summary>
/// <param name="Value">Milliohms above the typical group (charging test) or millivolts per day lost (overnight test).</param>
public sealed record GroupFlag(int Index, double Value, GroupFinding Finding);

/// <summary>What a check's parked tests found, kept with the check so trends can be followed across checks.</summary>
/// <param name="Resistance">Groups the charging test flagged; null when no charging test was measured.</param>
/// <param name="Overnight">Groups the overnight test flagged; null when there was no overnight comparison.</param>
public sealed record TestSummary(
    IReadOnlyList<GroupFlag>? Resistance,
    IReadOnlyList<GroupFlag>? Overnight,
    CheckStatus? Charger,
    string? ChargerSummary,
    CheckStatus? TwelveVolt,
    string? TwelveVoltSummary)
{
    public static TestSummary? From(SessionTests? tests)
    {
        if (tests is null)
            return null;

        var charger = tests.ChargerIfMeasured;
        var twelveVolt = tests.TwelveVoltIfMeasured;
        var summary = new TestSummary(
            tests.ChargeTest?.Flagged.Select(g => new GroupFlag(g.Index, g.ExcessMilliOhm, g.Finding)).ToList(),
            tests.Overnight?.Flagged.Select(g => new GroupFlag(g.Index, g.MvPerDay, g.Finding)).ToList(),
            charger?.Overall,
            charger is null ? null : BuyerCheck.Summarize(charger.Findings),
            twelveVolt?.Overall,
            twelveVolt is null ? null : BuyerCheck.Summarize(twelveVolt.Findings));
        return summary is { Resistance: null, Overnight: null, Charger: null, TwelveVolt: null } ? null : summary;
    }
}

/// <summary>One value per saved check, oldest first.</summary>
public sealed record Trend(string Title, string Unit, string Format, IReadOnlyList<(DateTimeOffset At, double Value)> Points)
{
    public string Latest => Points.Count == 0 ? Display.Missing : Text(Points[^1].Value);

    /// <summary>E.g. "88.9% → 87.6% over 340 days.", or the single reading's date.</summary>
    public string Change => Points.Count switch
    {
        0 => "No readings yet.",
        1 => $"Measured {Points[0].At.ToLocalTime().ToString("d", CultureInfo.CurrentCulture)}.",
        _ => $"{Text(Points[0].Value)} → {Text(Points[^1].Value)} " + ((Points[^1].At - Points[0].At).TotalDays < 1
            ? "within a day."
            : $"over {(Points[^1].At - Points[0].At).TotalDays.ToString("0", CultureInfo.CurrentCulture)} days."),
    };

    public string Text(double value) => value.ToString(Format, CultureInfo.CurrentCulture) + Unit;
}

/// <summary>A cell group flagged by at least one saved test of a car.</summary>
public sealed record RecurringGroup(int Index, int ResistanceFlags, int ResistanceTests, int OvernightFlags, int OvernightTests)
{
    public int Flags => ResistanceFlags + OvernightFlags;

    public string Describe()
    {
        var parts = new List<string>();
        if (ResistanceFlags > 0)
            parts.Add($"higher resistance in {ResistanceFlags} of {ResistanceTests} charging test{(ResistanceTests == 1 ? "" : "s")}");
        if (OvernightFlags > 0)
            parts.Add($"lost charge overnight in {OvernightFlags} of {OvernightTests} overnight test{(OvernightTests == 1 ? "" : "s")}");
        return $"Cell group {Index + 1}: {string.Join("; ", parts)}";
    }
}

/// <summary>Follows a car across its saved checks.</summary>
public static class CarTrends
{
    public static IReadOnlyList<Trend> Build(IEnumerable<CheckRecord> checks)
    {
        var ordered = checks.OrderBy(c => c.Ended).ToList();

        Trend Series(string title, string unit, string format, Func<CheckRecord, double?> value) => new(title, unit, format,
            ordered.Where(c => value(c) is not null).Select(c => (c.Ended, value(c)!.Value)).ToList());

        return
        [
            Series("State of health", "%", "0.0", c => c.StateOfHealthPercent),
            Series("Capacity", " kWh", "0.0", c => c.CurrentKWh),
            Series("Cell spread", " mV", "0", c => c.CellSpreadMv),
            Series("Insulation", " kΩ", "N0", c => c.IsolationKOhm),
            Series("Pack resistance", " mΩ", "0", c => c.PackResistanceMilliOhm),
        ];
    }

    /// <summary>Groups any saved test flagged, the most often flagged first.</summary>
    public static IReadOnlyList<RecurringGroup> Groups(IEnumerable<CheckRecord> checks)
    {
        var tests = checks.Select(c => c.Tests).OfType<TestSummary>().ToList();
        var resistanceTests = tests.Count(t => t.Resistance is not null);
        var overnightTests = tests.Count(t => t.Overnight is not null);
        var resistance = tests.SelectMany(t => t.Resistance ?? []).GroupBy(g => g.Index).ToDictionary(g => g.Key, g => g.Count());
        var overnight = tests.SelectMany(t => t.Overnight ?? []).GroupBy(g => g.Index).ToDictionary(g => g.Key, g => g.Count());

        return resistance.Keys.Union(overnight.Keys)
            .Select(i => new RecurringGroup(i, resistance.GetValueOrDefault(i), resistanceTests, overnight.GetValueOrDefault(i), overnightTests))
            .OrderByDescending(g => g.Flags).ThenBy(g => g.Index)
            .ToList();
    }
}

[JsonSourceGenerationOptions(UseStringEnumConverter = true)]
[JsonSerializable(typeof(TestSummary))]
[JsonSerializable(typeof(Dictionary<int, double>))]
internal sealed partial class StorageJsonContext : JsonSerializerContext;

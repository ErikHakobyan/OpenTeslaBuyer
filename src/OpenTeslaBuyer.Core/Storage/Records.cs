using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Buyer;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Storage;

/// <summary>A car the tool has seen, with the user's choices for it.</summary>
/// <param name="PackKey">Battery pack chosen by the user (pre-2021 Model S/X), see <see cref="PackEstimator.Packs"/>.</param>
/// <param name="OriginalKWh">Capacity when new typed in by the user.</param>
public sealed record CarRecord(
    string Vin,
    string? Model,
    int? ModelYear,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    string? PackKey,
    double? OriginalKWh,
    string? Notes);

/// <summary>A car with its number of saved checks and the latest one.</summary>
public sealed record CarSummary(CarRecord Car, int CheckCount, CheckRecord? Latest);

/// <summary>
/// One saved check: the key readings at the end of a connection, plus its report and text summary so either
/// can be shown again later without the car.
/// </summary>
public sealed record CheckRecord
{
    public long Id { get; init; }

    public string? Vin { get; init; }

    public string? Model { get; init; }

    public int? ModelYear { get; init; }

    public required DateTimeOffset Started { get; init; }

    public required DateTimeOffset Ended { get; init; }

    /// <summary>Where the data came from, e.g. "OBDLink / ELM327" or "Simulator: Model 3".</summary>
    public required string Source { get; init; }

    public string? Platform { get; init; }

    public double? OdometerKm { get; init; }

    public double? StateOfHealthPercent { get; init; }

    public double? OriginalKWh { get; init; }

    public string? OriginalSource { get; init; }

    public double? CurrentKWh { get; init; }

    public double? UsableKWh { get; init; }

    public double? CellSpreadMv { get; init; }

    public double? SocPercent { get; init; }

    public int ActiveAlerts { get; init; }

    /// <summary>Insulation resistance between the high-voltage system and the car body.</summary>
    public double? IsolationKOhm { get; init; }

    /// <summary>The pack's resistance from the charging test, when one was measured.</summary>
    public double? PackResistanceMilliOhm { get; init; }

    /// <summary>What the parked tests found.</summary>
    public TestSummary? Tests { get; init; }

    /// <summary>E.g. "1 attention, 7 pass".</summary>
    public string? BuyerSummary { get; init; }

    public string? SummaryText { get; init; }

    /// <summary>Loaded only when asked for (it is the largest column).</summary>
    public string? ReportHtml { get; init; }

    /// <summary>Builds the record for a finished connection.</summary>
    public static CheckRecord Create(
        BatteryData data,
        HealthReport health,
        IReadOnlyList<CheckItem> buyerCheck,
        IReadOnlyList<ActiveAlert> currentAlerts,
        IReadOnlyList<AlertHistoryEntry> alertHistory,
        AlertCoverage coverage,
        string source,
        DateTimeOffset started,
        DateTimeOffset ended,
        bool miles,
        SessionTests? tests = null)
    {
        var vin = data.Vin is { } v ? new VinInfo(v) : null;
        return new CheckRecord
        {
            Vin = data.Vin,
            Model = vin is null ? null : ReportWriter.ModelName(vin.Model),
            ModelYear = vin?.ModelYear,
            Started = started,
            Ended = ended,
            Source = source,
            Platform = data.Platform == VehiclePlatform.Auto ? null : VehicleProfiles.Describe(data.Platform),
            OdometerKm = data.OdometerKm,
            StateOfHealthPercent = health.StateOfHealthPercent,
            OriginalKWh = health.OriginalKWh,
            OriginalSource = health.OriginalKWh is null ? null : ReportWriter.OriginLabel(health),
            CurrentKWh = health.CurrentKWh,
            UsableKWh = health.UsableKWh,
            CellSpreadMv = health.CellSpreadMv,
            SocPercent = data.SocUiPercent,
            ActiveAlerts = currentAlerts.Count,
            IsolationKOhm = data.IsolationResistanceKOhm,
            PackResistanceMilliOhm = tests?.ChargeTest?.PackResistanceMilliOhm,
            Tests = TestSummary.From(tests),
            BuyerSummary = buyerCheck.Count == 0 ? null : BuyerCheck.Summarize(buyerCheck),
            SummaryText = ReportWriter.ToText(data, health, miles, currentAlerts, buyerCheck, tests),
            ReportHtml = ReportWriter.ToHtml(data, health, miles, ended, currentAlerts, alertHistory, coverage, buyerCheck, tests),
        };
    }

    /// <summary>Whether a connection produced enough to be worth saving.</summary>
    public static bool IsWorthSaving(BatteryData data) =>
        data.FrameCount > 0 && (data.Vin is not null || data.NominalFullPackKWh is not null);
}

/// <summary>A CAN recording (candump log file) and what is known about it.</summary>
public sealed record RecordingRecord(
    string Path,
    string? Vin,
    DateTimeOffset Started,
    DateTimeOffset? Ended,
    long? Frames,
    long? SizeBytes,
    string? Source,
    string? Notes);

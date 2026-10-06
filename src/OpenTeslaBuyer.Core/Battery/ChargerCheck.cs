using System.Globalization;
using OpenTeslaBuyer.Core.Buyer;

namespace OpenTeslaBuyer.Core.Battery;

/// <summary>The onboard charger, as measured while charging from an AC supply.</summary>
/// <param name="Measured">How much steady AC charging the figures average.</param>
/// <param name="EfficiencyPercent">The share of the supply's power that reached the battery.</param>
/// <param name="PhasesUsed">How many of the charger's three phase modules were on.</param>
public sealed record ChargerReport(
    bool ChargingNow,
    TimeSpan Measured,
    double? AcVolts,
    double? AcAmps,
    double? AcLimitAmps,
    double? AcPowerKw,
    double? BatteryPowerKw,
    double? EfficiencyPercent,
    string? Supply,
    string? Charger,
    int? PhasesUsed,
    IReadOnlyList<CheckItem> Findings)
{
    public CheckStatus Overall => BuyerCheck.Overall(Findings);

    public bool Measuring => Measured > TimeSpan.Zero;

    public string ToText() => "Onboard charger: " + string.Join("; ", Findings.Where(f => f.Status != CheckStatus.NotAvailable)
        .Select(f => $"{BuyerCheck.Describe(f.Status)} - {f.Title}: {f.Summary}"));
}

/// <summary>
/// Watches AC charging (wall connector, home or public AC charger) on the Model 3 platform: faults the charger reports,
/// how many of its phase modules work, how much of the allowed current it draws, and how much of the supply's power
/// reaches the battery. A failed phase module is a common and costly fault that only shows as slower charging.
/// </summary>
public sealed class ChargerCheck
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);

    /// <summary>Charging takes a few seconds to ramp up; the figures start after that.</summary>
    internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

    /// <summary>The figures average the latest minute of charging.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    internal static readonly TimeSpan MinimumMeasured = TimeSpan.FromSeconds(30);

    /// <summary>A charger normally delivers 85–93% of the supply's power to the battery; less suggests losses (or other loads).</summary>
    internal const double GoodEfficiency = 85;

    /// <summary>Drawing this share of the allowed current counts as full rate.</summary>
    internal const double FullRateShare = 0.85;

    private readonly Queue<Sample> _samples = new();
    private DateTimeOffset? _last;
    private DateTimeOffset? _chargingSince;
    private bool _charging;
    private bool _everCharged;
    private bool _faulted;

    /// <param name="packAmps">The pack current, positive into the pack (from the charging test).</param>
    public void Observe(BatteryData data, DateTimeOffset time, double? packAmps)
    {
        if (_last is { } last && time >= last && time - last < SampleInterval)
            return;

        _last = time;
        if (data.ChargerHvStatus == 3 || data.ChargerState == 8)
            _faulted = true;

        _charging = data.AcVolts >= 80 && data.AcAmps >= 1;
        if (!_charging)
        {
            _chargingSince = null;
            return;
        }

        if (_chargingSince is null)
        {
            _chargingSince = time;
            _samples.Clear(); // a new charge: measure it afresh
        }

        _everCharged = true;
        if (time - _chargingSince < SettleTime)
            return;

        var batteryKw = packAmps is { } amps && data.PackVoltage is { } volts ? amps * volts / 1000 : (double?)null;
        _samples.Enqueue(new Sample(time, data.AcVolts!.Value, data.AcAmps!.Value, data.AcInputKw, data.AcCurrentLimitAmps, batteryKw,
            data.ChargerPhases, data.GridConfig, data.SocUiPercent));
        while (time - _samples.Peek().Time > Window)
            _samples.Dequeue();
    }

    public ChargerReport Report(BatteryData data)
    {
        var samples = _samples.ToList();
        var measured = samples.Count < 2 ? TimeSpan.Zero : samples[^1].Time - samples[0].Time;
        var enough = measured >= MinimumMeasured;

        double? Average(Func<Sample, double?> value)
        {
            var values = samples.Select(value).OfType<double>().ToList();
            return values.Count == 0 ? null : values.Average();
        }

        var acVolts = Average(s => s.AcVolts);
        var acAmps = Average(s => s.AcAmps);
        var limit = Average(s => s.LimitAmps);
        var acKw = Average(s => s.InputKw) ?? (acVolts * acAmps / 1000);
        var batteryKw = Average(s => s.BatteryKw);
        double? efficiency = enough && acKw > 1 && batteryKw is { } b ? b / acKw * 100 : null;
        var latest = samples.LastOrDefault();
        var grid = latest?.GridConfig ?? data.GridConfig;
        int? phases = (latest?.Phases ?? data.ChargerPhases) is { } bits ? int.PopCount(bits) : null;

        var supply = grid switch
        {
            1 => "Single-phase",
            2 => "Three-phase",
            3 => "Three-phase (delta)",
            _ => null,
        };
        if (supply is not null && acVolts is { } v)
            supply += $", {v.ToString("0", CultureInfo.CurrentCulture)} V";

        var charger = data.ChargerVariant switch
        {
            0 => "48 A single-phase charger",
            1 => "32 A single-phase charger",
            2 => "Three-phase charger",
            _ => null,
        };

        List<CheckItem> findings =
        [
            Faults(),
            Phases(enough, grid, phases),
            Current(enough, acAmps, limit, latest?.Soc),
            Efficiency(efficiency, acKw, batteryKw),
        ];
        return new ChargerReport(_charging, measured, acVolts, acAmps, limit, acKw, batteryKw, efficiency, supply, charger, phases, findings);
    }

    private CheckItem Faults()
    {
        const string key = "chargerFaults", title = "Charger faults";
        if (_faulted)
            return new(key, title, CheckStatus.Fail, "The onboard charger reported a fault during this connection.",
                "Check the car's alerts for the charger (PCS) and try another charger. A charger that keeps faulting needs a service visit.");
        return _everCharged
            ? new(key, title, CheckStatus.Pass, "No faults while charging.")
            : new(key, title, CheckStatus.NotAvailable, "Not charged from an AC supply during this connection.");
    }

    private static CheckItem Phases(bool enough, int? grid, int? phases)
    {
        const string key = "chargerPhases", title = "Phases";
        if (!enough || grid is not (1 or 2 or 3) || phases is not { } count)
            return new(key, title, CheckStatus.NotAvailable, "Measured once AC charging has run for half a minute.");
        if (grid == 1)
            return new(key, title, CheckStatus.Pass, "Single-phase supply.");
        return count >= 3
            ? new(key, title, CheckStatus.Pass, "Charging on all three phases.")
            : new(key, title, CheckStatus.Attention, $"Charging on {count} of 3 phases.",
                "On a three-phase supply all three of the charger's phase modules should work. A missing phase means one module, or the "
                + "supply, has failed; charging on another three-phase charger tells which. A failed module is replaced at a service centre.");
    }

    private static CheckItem Current(bool enough, double? amps, double? limit, double? soc)
    {
        const string key = "chargerCurrent", title = "Current drawn";
        if (!enough || amps is not { } drawn || limit is not { } allowed || allowed < 6)
            return new(key, title, CheckStatus.NotAvailable, "Measured once AC charging has run for half a minute.");

        var text = $"Drawing {drawn.ToString("0.0", CultureInfo.CurrentCulture)} A of the {allowed.ToString("0", CultureInfo.CurrentCulture)} A allowed";
        if (drawn >= allowed * FullRateShare)
            return new(key, title, CheckStatus.Pass, text + ".");
        if (soc >= 90)
            return new(key, title, CheckStatus.Pass, text + ", as expected while the battery nears full.");
        return new(key, title, CheckStatus.Attention, text + ".",
            "That's expected if the charging current is turned down in the car's charging settings. Otherwise the charger may be "
            + "holding back, for example because it's too warm or a module is weak.");
    }

    private static CheckItem Efficiency(double? efficiency, double? acKw, double? batteryKw)
    {
        const string key = "chargerEfficiency", title = "Power reaching the battery";
        if (efficiency is not { } percent)
            return new(key, title, CheckStatus.NotAvailable, "Measured once AC charging has run for half a minute.");

        var text = $"{percent.ToString("0", CultureInfo.CurrentCulture)}% of the supply's power reaches the battery "
                   + $"({batteryKw!.Value.ToString("0.0", CultureInfo.CurrentCulture)} of {acKw!.Value.ToString("0.0", CultureInfo.CurrentCulture)} kW)";
        return percent >= GoodEfficiency
            ? new(key, title, CheckStatus.Pass, text + ".")
            : new(key, title, CheckStatus.Attention, text + ", less than the usual 85–93%.",
                "Some power goes to the car itself while it charges: heating the battery in the cold, or running the climate. "
                + "If neither is on, the charger may be losing power and is worth a closer look.");
    }

    private sealed record Sample(
        DateTimeOffset Time, double AcVolts, double AcAmps, double? InputKw, double? LimitAmps, double? BatteryKw, int? Phases, int? GridConfig, double? Soc);
}

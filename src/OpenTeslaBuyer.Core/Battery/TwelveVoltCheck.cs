using System.Globalization;
using OpenTeslaBuyer.Core.Buyer;

namespace OpenTeslaBuyer.Core.Battery;

/// <summary>The 12 V system: the DC-DC converter that supplies it while the car is awake, and the low-voltage battery behind it.</summary>
/// <param name="LowestBeforeSupport">The lowest 12 V reading before the DC-DC converter took over, if the car woke while connected.</param>
/// <param name="Lithium">A 16 V lithium-ion low-voltage battery (2021+ Model S/X, 2022+ Model 3/Y) rather than 12 V lead-acid.</param>
public sealed record TwelveVoltReport(
    double? Volts,
    double? DcDcAmps,
    bool DcDcSupplying,
    double? LowestBeforeSupport,
    bool Lithium,
    string? BatteryType,
    int? CarAgeYears,
    IReadOnlyList<CheckItem> Findings)
{
    public CheckStatus Overall => BuyerCheck.Overall(Findings);

    public string ToText() => "12 V system: " + string.Join("; ", Findings.Where(f => f.Status != CheckStatus.NotAvailable)
        .Select(f => $"{BuyerCheck.Describe(f.Status)} - {f.Title}: {f.Summary}"));
}

/// <summary>
/// Follows the 12 V system through a connection. 12 V problems are among the most common reasons a Tesla won't wake:
/// a failing DC-DC converter stops topping the battery up, and an ageing lead-acid battery sags as soon as the
/// converter isn't supplying it.
/// </summary>
public sealed class TwelveVoltCheck
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    internal const int RecentSamples = 60;

    /// <summary>Above this the DC-DC converter is treated as supplying the 12 V system.</summary>
    internal const double SupplyingAmps = 2;

    /// <summary>Above this the low-voltage system is the 16 V lithium-ion kind.</summary>
    internal const double LithiumVolts = 15.0;

    /// <summary>An awake car typically draws 15–50 A from the converter; much more suggests a drain or a battery being recharged hard.</summary>
    internal const double HighLoadAmps = 70;

    /// <summary>Lead-acid 12 V batteries in these cars usually last 3–5 years.</summary>
    internal const int LeadAcidYears = 4;

    private readonly Queue<double> _supportedVolts = new();
    private readonly Queue<double> _amps = new();
    private DateTimeOffset? _last;
    private double? _lowestBeforeSupport;
    private bool _faulted;
    private int _supportingSamples;
    private int _limitedSamples;

    public void Observe(BatteryData data, DateTimeOffset time)
    {
        if (_last is { } last && time >= last && time - last < SampleInterval)
            return;

        _last = time;
        if (data.DcDcFaulted == true || data.DcDcState == 6)
            _faulted = true;

        var supplying = Supplying(data);
        var volts = data.TwelveVoltVolts ?? data.DcDcVolts;
        if (!supplying)
        {
            if (volts is { } resting)
                _lowestBeforeSupport = Math.Min(_lowestBeforeSupport ?? resting, resting);
            return;
        }

        _supportingSamples++;
        if (data.DcDcLimited == true)
            _limitedSamples++;
        if ((data.DcDcVolts ?? data.TwelveVoltVolts) is { } supplied)
            Add(_supportedVolts, supplied);
        if (data.DcDcAmps is { } amps)
            Add(_amps, amps);
    }

    public TwelveVoltReport Report(BatteryData data)
    {
        double? supported = _supportedVolts.Count == 0 ? null : CellDrift.Median(_supportedVolts);
        double? amps = _amps.Count == 0 ? null : CellDrift.Median(_amps);
        var lithium = supported >= LithiumVolts;
        var type = data.CarInfo.GetValueOrDefault("twelveVBattery");
        var age = CarAgeYears(data);

        List<CheckItem> findings =
        [
            DcDc(supported, amps, lithium),
            AtWake(lithium),
            Load(amps),
            Battery(lithium, type, age),
        ];
        return new TwelveVoltReport(data.TwelveVoltVolts ?? data.DcDcVolts, data.DcDcAmps, Supplying(data), _lowestBeforeSupport, lithium, type, age, findings);
    }

    private static bool Supplying(BatteryData data) => data.DcDcState == 1 || data.DcDcAmps > SupplyingAmps;

    private CheckItem DcDc(double? supported, double? amps, bool lithium)
    {
        const string key = "dcdc", title = "DC-DC converter";
        if (_faulted)
            return new(key, title, CheckStatus.Fail, "The DC-DC converter reported a fault.",
                "It keeps the 12 V system charged while the car is awake; if it fails, the 12 V battery runs down and the car stops waking. Have it checked.");
        if (supported is not { } volts)
            return new(key, title, CheckStatus.NotAvailable, "Waiting for the DC-DC converter.");

        var text = $"Supplying {volts.ToString("0.0", CultureInfo.CurrentCulture)} V"
                   + (amps is { } a ? $" at {a.ToString("0", CultureInfo.CurrentCulture)} A" : "");
        if (volts < (lithium ? 14.8 : 13.0))
            return new(key, title, CheckStatus.Attention, text + ", lower than usual.",
                lithium ? "It normally holds about 15–16 V." : "It normally holds about 13.5–15 V while the car is awake.");
        if (_supportingSamples >= 10 && _limitedSamples > _supportingSamples * 0.3)
            return new(key, title, CheckStatus.Attention, text + ", but it limited its output for much of the time.",
                "The converter caps its output when it's too hot or overloaded. If this repeats, have the 12 V system checked.");
        return new(key, title, CheckStatus.Pass, text + ".");
    }

    private CheckItem AtWake(bool lithium)
    {
        const string key = "lvBatteryWake", title = "12 V battery at wake-up";
        if (lithium)
            return new(key, title, CheckStatus.NotAvailable, "Not judged: a lithium-ion low-voltage battery holds its voltage until it's nearly empty.");
        if (_lowestBeforeSupport is not { } volts)
            return new(key, title, CheckStatus.NotAvailable, "Not seen: the car was already awake.",
                "Connect while the car is asleep (locked and left alone for 15 minutes or more), then open a door: the tool catches the battery before the converter takes over.");

        var text = $"{volts.ToString("0.0", CultureInfo.CurrentCulture)} V before the converter took over";
        return volts switch
        {
            >= 12.4 => new(key, title, CheckStatus.Pass, text + ": well charged."),
            >= 12.0 => new(key, title, CheckStatus.Attention, text + ": partly discharged or ageing.",
                "A healthy, charged lead-acid battery rests at about 12.6 V. Recheck after a drive; if it stays low, plan a replacement."),
            _ => new(key, title, CheckStatus.Fail, text + ": weak or deeply discharged.",
                "It may soon leave the car unable to wake or open. Have the 12 V battery tested and probably replaced."),
        };
    }

    private static CheckItem Load(double? amps)
    {
        const string key = "lvLoad", title = "12 V load";
        if (amps is not { } a)
            return new(key, title, CheckStatus.NotAvailable, "Waiting for the DC-DC converter.");

        var text = $"{a.ToString("0", CultureInfo.CurrentCulture)} A from the converter";
        return a > HighLoadAmps
            ? new(key, title, CheckStatus.Attention, text + ", more than an awake car usually needs.",
                "About 15–50 A is typical. More suggests something drawing power (an accessory, a stuck fan or heater) or a 12 V battery being recharged hard.")
            : new(key, title, CheckStatus.Pass, text + ", normal for an awake car.");
    }

    private static CheckItem Battery(bool lithium, string? type, int? age)
    {
        const string key = "lvBattery", title = "12 V battery";
        if (lithium)
            return new(key, title, CheckStatus.Pass, "Lithium-ion low-voltage battery, meant to last the life of the car.");
        if (age is not { } years)
            return new(key, title, CheckStatus.NotAvailable, type is null ? "Battery type and car age unknown." : $"{type}; car age unknown.");

        var what = (type ?? "Lead-acid") + $", car about {years} year{(years == 1 ? "" : "s")} old";
        return years >= LeadAcidYears
            ? new(key, title, CheckStatus.Attention, what + ".",
                "Lead-acid 12 V batteries usually last 3–5 years. Unless it has been replaced, ask when; budget for a new one if it's the original.")
            : new(key, title, CheckStatus.Pass, what + ".");
    }

    /// <summary>From the gateway's build date, else the VIN's model year.</summary>
    private static int? CarAgeYears(BatteryData data)
    {
        var today = DateTime.Today;
        if (data.CarInfo.TryGetValue("birthday", out var birthday)
            && DateTime.TryParseExact(birthday, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var built))
            return Math.Max(0, (int)((today - built).TotalDays / 365.25));
        if (data.Vin is { } vin && new Vehicles.VinInfo(vin).ModelYear is { } year)
            return Math.Max(0, today.Year - year);
        return null;
    }

    private static void Add(Queue<double> queue, double value)
    {
        queue.Enqueue(value);
        if (queue.Count > RecentSamples)
            queue.Dequeue();
    }
}

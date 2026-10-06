using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Buyer;

namespace OpenTeslaBuyer.App.ViewModels;

/// <summary>
/// The Tests page: checks that run with the car parked, each on its own tab. They all run in Core on every connection,
/// whichever page is open; the tabs show their progress and results.
/// </summary>
public sealed partial class TestsViewModel : ObservableObject, IPage
{
    private readonly ShellViewModel _shell;

    [ObservableProperty]
    private bool _isConnected;

    public TestsViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Charging = new ChargingViewModel(shell);
        Overnight = new OvernightViewModel(shell.Diagnostics);
        Charger = new ChargerTabViewModel(shell.Diagnostics);
        TwelveVolt = new TwelveVoltViewModel(shell.Diagnostics);
        shell.Diagnostics.Refreshed += (_, _) =>
        {
            if (shell.IsTests)
                Update();
        };
    }

    public ChargingViewModel Charging { get; }

    public OvernightViewModel Overnight { get; }

    public ChargerTabViewModel Charger { get; }

    public TwelveVoltViewModel TwelveVolt { get; }

    public void Activate()
    {
        Charging.Activate();
        Update();
    }

    [RelayCommand]
    private void GoToDiagnostics() => _shell.SelectedPage = _shell.Pages[0];

    private void Update()
    {
        IsConnected = _shell.Diagnostics.IsConnected;
        Charging.Update();
        Overnight.Update();
        Charger.Update();
        TwelveVolt.Update();
    }

    /// <summary>Replaces a collection's items only when they changed, so lists don't flicker on every refresh.</summary>
    internal static void Replace<T>(ObservableCollection<T> collection, IReadOnlyList<T> items)
    {
        if (collection.SequenceEqual(items))
            return;

        collection.Clear();
        foreach (var item in items)
            collection.Add(item);
    }
}

/// <summary>The overnight test: compares this connection's parked reading of every cell group with an earlier one.</summary>
public sealed partial class OvernightViewModel(MainViewModel diagnostics) : ObservableObject
{
    private (string? Vin, DateTimeOffset? Snapshot) _readingsFor;

    [ObservableProperty]
    private string _statusText = "";

    /// <summary>"idle", "resting", "charging" or "discharging", as on the charging test tab.</summary>
    [ObservableProperty]
    private string _activityKind = "idle";

    [ObservableProperty]
    private string _activityText = "Not connected";

    [ObservableProperty]
    private string _readingsText = "";

    [ObservableProperty]
    private bool _hasResult;

    [ObservableProperty]
    private string _resultTitle = "No comparison yet";

    /// <summary>"none", "ok", "attention" or "critical".</summary>
    [ObservableProperty]
    private string _resultKind = "none";

    [ObservableProperty]
    private string _resultDetail = "";

    [ObservableProperty]
    private string _earlierText = Display.Missing;

    [ObservableProperty]
    private string _laterText = Display.Missing;

    [ObservableProperty]
    private string _elapsedText = Display.Missing;

    [ObservableProperty]
    private string? _flatNote;

    public ObservableCollection<FlaggedGroup> Flagged { get; } = [];

    /// <summary>How much charge each group lost compared with the typical group.</summary>
    public ObservableCollection<GroupBar> Bars { get; } = [];

    public void Update()
    {
        var status = diagnostics.ChargeTest;
        var result = diagnostics.Tests.Overnight;
        var snapshot = status?.ParkedSnapshot;
        var connected = diagnostics.IsConnected;

        (ActivityText, ActivityKind) = !connected || status is null
            ? ("Not connected", "idle")
            : status.Activity switch
            {
                PackActivity.Resting => ("Resting", "resting"),
                PackActivity.Charging => ("Charging", "charging"),
                PackActivity.Discharging => ("Discharging", "discharging"),
                _ => ("Waiting for data", "idle"),
            };

        StatusText = (connected, snapshot, result, diagnostics.EarlierSnapshot) switch
        {
            (_, _, not null, _) => $"Compared this reading with the one from {When(result!.Earlier.TakenAt)}.",
            (false, _, _, _) => "Connect with the car parked. After about a minute at rest the tool saves a reading of every cell group; "
                                + "connect again at least 4 hours later (ideally the next morning) and it compares the two.",
            (true, null, _, _) when status?.Activity == PackActivity.Charging =>
                "Charging: the reading is only taken before charging, while the cells are settled. Connect again before the next charge.",
            (true, null, _, _) when status?.Activity == PackActivity.Resting => "Taking a reading: keep the car parked and idle for about a minute.",
            (true, null, _, _) => "Waiting for the car to rest: parked, not charging, climate off.",
            (true, not null, null, null) => $"Reading saved at {When(snapshot!.TakenAt)}. Connect again at least 4 hours later, ideally tomorrow morning, to compare.",
            (true, not null, null, not null) => "Couldn't compare with the earlier reading: too few cell groups matched.",
        };

        UpdateReadings(diagnostics.LatestData.Vin, snapshot?.TakenAt);
        UpdateResult(result);
    }

    private void UpdateReadings(string? vin, DateTimeOffset? snapshot)
    {
        if (_readingsFor == (vin, snapshot))
            return;

        _readingsFor = (vin, snapshot);
        if (vin is null)
        {
            ReadingsText = "";
            return;
        }

        var readings = diagnostics.Database.ListSnapshots(vin);
        ReadingsText = readings.Count == 0
            ? "No parked readings saved for this car yet."
            : $"{readings.Count} parked reading{(readings.Count == 1 ? "" : "s")} saved for this car; the latest from {When(readings[0].TakenAt)}.";
    }

    private void UpdateResult(DriftResult? result)
    {
        HasResult = result is not null;
        if (result is null)
        {
            ResultKind = "none";
            ResultTitle = "No comparison yet";
            ResultDetail = "A result appears once this connection's reading can be compared with one taken at least 4 hours earlier.";
            Flagged.Clear();
            Bars.Clear();
            EarlierText = LaterText = ElapsedText = Display.Missing;
            FlatNote = null;
            return;
        }

        ResultKind = result.Worst switch
        {
            GroupFinding.MuchHigher => "critical",
            GroupFinding.Higher => "attention",
            _ => "ok",
        };
        ResultTitle = result.Summary;
        ResultDetail = result.Flagged.Count == 0
            ? "No group lost more charge than the others while the car stood. Groups that hold their charge alike are a healthy sign."
            : "These groups lost charge faster than the rest while the car stood: a sign of an internal leak (high self-discharge) in a cell, "
              + "which tends to get worse. Repeat the test over another night; a group that keeps falling behind is worth showing to a service centre.";
        FlatNote = result.FlatVoltagePack
            ? "This pack's voltage hardly changes with charge (it looks like an iron-phosphate pack), so only large losses show here."
            : null;

        TestsViewModel.Replace(Flagged, result.Flagged.Take(8).Select(g => new FlaggedGroup(
            $"Cell group {g.Index + 1}",
            $"{Math.Abs(g.ChangeMv).ToString("0.0", CultureInfo.CurrentCulture)} mV below the others · {Math.Abs(g.MvPerDay).ToString("0.0", CultureInfo.CurrentCulture)} mV per day",
            g.Finding == GroupFinding.MuchHigher ? "critical" : "attention")).ToList());

        if (Bars.Count != result.Groups.Count)
        {
            Bars.Clear();
            foreach (var group in result.Groups)
                Bars.Add(new GroupBar(group.Index));
        }

        // Taller bars lost more charge; at least 2 mV of scale so ordinary wobbles don't look dramatic.
        var drops = result.Groups.Select(g => -g.ChangeMv).ToList();
        var low = drops.Min();
        var high = Math.Max(drops.Max(), low + 2);
        foreach (var (bar, group) in Bars.Zip(result.Groups))
        {
            bar.Height = BrickBar.MinHeight + (BrickBar.MaxHeight - BrickBar.MinHeight) * (-group.ChangeMv - low) / (high - low);
            bar.Level = group.Finding switch
            {
                GroupFinding.MuchHigher => "much-higher",
                GroupFinding.Higher => "higher",
                _ => "normal",
            };
            bar.ToolTip = $"Cell group {group.Index + 1}: {group.ChangeMv.ToString("+0.0;-0.0", CultureInfo.CurrentCulture)} mV against the typical group "
                          + $"({group.MvPerDay.ToString("+0.0;-0.0", CultureInfo.CurrentCulture)} mV per day)";
        }

        EarlierText = Describe(result.Earlier);
        LaterText = Describe(result.Later);
        ElapsedText = DriftResult.Span(result.Elapsed);
    }

    private static string Describe(CellSnapshot snapshot)
    {
        var parts = new List<string> { When(snapshot.TakenAt) };
        if (snapshot.SocPercent is not null)
            parts.Add(Display.Percent(snapshot.SocPercent, "0") + " charged");
        if (snapshot.TempMinC is not null || snapshot.TempMaxC is not null)
            parts.Add($"{Display.Number(snapshot.TempMinC, "0")}–{Display.Number(snapshot.TempMaxC, "0")} °C");
        return string.Join(" · ", parts);
    }

    private static string When(DateTimeOffset time) => time.ToLocalTime().ToString("ddd HH:mm", CultureInfo.CurrentCulture);
}

/// <summary>The onboard charger check: measured while charging from an AC supply.</summary>
public sealed partial class ChargerTabViewModel : ObservableObject
{
    private readonly MainViewModel _diagnostics;
    private readonly Metric _charger = new("Charger");
    private readonly Metric _supply = new("Supply");
    private readonly Metric _current = new("AC current", "What the car draws, and the most it's allowed to.");
    private readonly Metric _fromSupply = new("From the supply");
    private readonly Metric _toBattery = new("Into the battery");
    private readonly Metric _efficiency = new("Reaches the battery", "The share of the supply's power that ends up in the battery.");

    [ObservableProperty]
    private string _statusText = "";

    [ObservableProperty]
    private string? _platformNote;

    public ChargerTabViewModel(MainViewModel diagnostics)
    {
        _diagnostics = diagnostics;
        Metrics = [_charger, _supply, _current, _fromSupply, _toBattery, _efficiency];
    }

    public ObservableCollection<Metric> Metrics { get; }

    public ObservableCollection<CheckItem> Findings { get; } = [];

    public void Update()
    {
        var report = _diagnostics.Tests.Charger;
        var data = _diagnostics.LatestData;
        var window = (int)ChargerCheck.Window.TotalSeconds;
        StatusText = (_diagnostics.IsConnected, report) switch
        {
            (false, { Measuring: true }) => "Showing the last minute of AC charging from this connection.",
            (false, _) => "Connect, then charge from an AC supply: a home charger, wall connector or public AC charger. "
                          + "(Superchargers bypass the onboard charger, so they don't test it.)",
            (true, { ChargingNow: true, Measuring: false }) => "AC charging started: measuring once it has settled…",
            (true, { ChargingNow: true } r) when r.Measured.TotalSeconds < window - 2 =>
                $"AC charging: measuring ({r.Measured.TotalSeconds.ToString("0", CultureInfo.CurrentCulture)} of {window} s).",
            (true, { ChargingNow: true }) => "Measured over the latest minute of AC charging.",
            (true, { Measuring: true }) => "Charging stopped; showing its last minute.",
            _ => "Not charging from an AC supply. Plug into a home charger, wall connector or public AC charger while connected.",
        };
        PlatformNote = data.Platform == VehiclePlatform.LegacyModelSX
            ? "The onboard charger isn't decoded on the 2012–2021 Model S/X yet, so this check can't run on this car."
            : null;

        _charger.Value = report?.Charger ?? Display.Missing;
        _supply.Value = report?.Supply ?? Display.Missing;
        _current.Value = report?.AcAmps is { } amps
            ? $"{Display.Amps(amps)}{(report.AcLimitAmps is { } limit ? " of " + limit.ToString("0", CultureInfo.CurrentCulture) + " A" : "")}"
            : Display.Missing;
        _fromSupply.Value = Display.Kilowatts(report?.AcPowerKw);
        _toBattery.Value = Display.Kilowatts(report?.BatteryPowerKw);
        _efficiency.Value = Display.Percent(report?.EfficiencyPercent, "0");
        TestsViewModel.Replace(Findings, report?.Findings ?? []);
    }
}

/// <summary>The 12 V check: the DC-DC converter and the low-voltage battery.</summary>
public sealed partial class TwelveVoltViewModel : ObservableObject
{
    private readonly MainViewModel _diagnostics;
    private readonly Metric _volts = new("12 V now");
    private readonly Metric _converter = new("DC-DC converter", "Supplies the 12 V system from the main battery while the car is awake.");
    private readonly Metric _amps = new("Converter current");
    private readonly Metric _atWake = new("Before the converter took over", "The 12 V battery on its own, caught while the car was waking.");
    private readonly Metric _battery = new("12 V battery");
    private readonly Metric _age = new("Car age");

    [ObservableProperty]
    private string _statusText = "";

    public TwelveVoltViewModel(MainViewModel diagnostics)
    {
        _diagnostics = diagnostics;
        Metrics = [_volts, _converter, _amps, _atWake, _battery, _age];
    }

    public ObservableCollection<Metric> Metrics { get; }

    public ObservableCollection<CheckItem> Findings { get; } = [];

    public void Update()
    {
        var report = _diagnostics.Tests.TwelveVolt;
        StatusText = _diagnostics.IsConnected
            ? "To also judge the 12 V battery itself, connect while the car is asleep (locked and left alone for 15 minutes or more), then open a door."
            : "Connect to check the 12 V system.";
        _volts.Value = Display.Volts(report?.Volts);
        _converter.Value = report is null ? Display.Missing : report.DcDcSupplying ? "Supplying" : "Not supplying";
        _amps.Value = Display.Amps(report?.DcDcAmps);
        _atWake.Value = Display.Volts(report?.LowestBeforeSupport, "0.00");
        _battery.Value = report is null ? Display.Missing : report.Lithium ? "Lithium-ion (16 V)" : report.BatteryType ?? "Lead-acid";
        _age.Value = report?.CarAgeYears is { } years ? $"{years} year{(years == 1 ? "" : "s")}" : Display.Missing;
        TestsViewModel.Replace(Findings, report?.Findings ?? []);
    }
}

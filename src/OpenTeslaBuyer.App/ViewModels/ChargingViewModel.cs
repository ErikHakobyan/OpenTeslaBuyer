using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTeslaBuyer.Core.Battery;

namespace OpenTeslaBuyer.App.ViewModels;

/// <summary>
/// The charging test tab. The test itself runs in Core on every connection (<see cref="ChargeTest"/>), whichever page
/// is open; this tab shows its progress, the cell groups right now and the result.
/// </summary>
public sealed partial class ChargingViewModel : ObservableObject
{
    private readonly ShellViewModel _shell;
    private readonly Metric _current = new("Pack current", "Positive while charging.");
    private readonly Metric _power = new("Pack power", "Positive while charging.");
    private readonly Metric _voltage = new("Pack voltage");
    private readonly Metric _soc = new("State of charge");
    private readonly Metric _temperature = new("Pack temperature");
    private readonly Metric _added = new("Added this session");

    [ObservableProperty]
    private bool _isConnected;

    [ObservableProperty]
    private string _activityText = "Not connected";

    /// <summary>"idle", "resting", "charging" or "discharging"; picks the status dot's colour.</summary>
    [ObservableProperty]
    private string _activityKind = "idle";

    [ObservableProperty]
    private string _progressText = "";

    [ObservableProperty]
    private string? _estimateNote;

    [ObservableProperty]
    private bool _hasLiveGroups;

    [ObservableProperty]
    private string _liveSummary = "";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(CopyResultCommand))]
    private bool _hasResult;

    [ObservableProperty]
    private string _resultTitle = "";

    /// <summary>"none", "ok", "attention" or "critical"; picks the result's accent colour.</summary>
    [ObservableProperty]
    private string _resultKind = "none";

    [ObservableProperty]
    private string _resultDetail = "";

    [ObservableProperty]
    private string _packResistanceText = Display.Missing;

    [ObservableProperty]
    private string _averageGroupText = Display.Missing;

    [ObservableProperty]
    private string _measurementsText = Display.Missing;

    [ObservableProperty]
    private string _conditionsText = Display.Missing;

    [ObservableProperty]
    private string? _feedback;

    public ChargingViewModel(ShellViewModel shell)
    {
        _shell = shell;
        Metrics = [_current, _power, _voltage, _soc, _temperature, _added];
    }

    public ObservableCollection<Metric> Metrics { get; }

    /// <summary>Cell group voltages right now; groups the result flagged are coloured.</summary>
    public ObservableCollection<GroupBar> LiveGroups { get; } = [];

    /// <summary>Each group's resistance compared with the typical group.</summary>
    public ObservableCollection<GroupBar> ResultGroups { get; } = [];

    public ObservableCollection<FlaggedGroup> Flagged { get; } = [];

    public void Activate()
    {
        Feedback = null;
        Update();
    }

    [RelayCommand]
    private void StartOver()
    {
        _shell.Diagnostics.RestartChargeTest();
        Feedback = "Started over. Measurements from before are discarded.";
        Update();
    }

    [RelayCommand(CanExecute = nameof(HasResult))]
    private void CopyResult()
    {
        if (_shell.Diagnostics.ChargeTest?.Result is { } result)
            Feedback = Ui.Copy(result.ToText()) ? "Result copied." : "Clipboard is busy; try again.";
    }

    internal void Update()
    {
        var diagnostics = _shell.Diagnostics;
        var status = diagnostics.ChargeTest;
        var data = diagnostics.LatestData;
        IsConnected = diagnostics.IsConnected;

        UpdateStatus(status);
        _current.Value = IsConnected ? Display.Amps(status?.Amps) : Display.Missing;
        _power.Value = IsConnected ? Display.Kilowatts(status?.PowerKw) : Display.Missing;
        _voltage.Value = Display.Volts(data.PackVoltage);
        _soc.Value = Display.Percent(data.SocUiPercent);
        _temperature.Value = data.TempMinC is null && data.TempMaxC is null
            ? Display.Missing
            : $"{Display.Number(data.TempMinC, "0.0")} – {Display.Celsius(data.TempMaxC)}";
        _added.Value = Display.KWh(status?.EnergyAddedKWh, "0.00");

        UpdateLiveGroups(data, status?.Result);
        UpdateResult(status?.Result);
    }

    private void UpdateStatus(ChargeTestStatus? status)
    {
        if (!IsConnected || status is null)
        {
            ActivityText = "Not connected";
            ActivityKind = "idle";
            EstimateNote = null;
            ProgressText = status?.Result is not null
                ? "Showing the last test. Connect on the Diagnostics page to run another."
                : "Connect on the Diagnostics page with the car parked. The test then runs in the background, whichever page is open.";
            return;
        }

        var (text, kind) = status.Activity switch
        {
            PackActivity.Resting => ("Resting", "resting"),
            PackActivity.Charging => ("Charging", "charging"),
            PackActivity.Discharging => ("Discharging", "discharging"),
            _ => ("Waiting for data", "idle"),
        };
        ActivityText = status.Activity == PackActivity.Unknown ? text : $"{text} · {Ui.Duration(status.ActivityDuration)}";
        ActivityKind = kind;
        ProgressText = status.Progress;
        EstimateNote = status.CurrentEstimated && status.Amps is not null
            ? "This car doesn't broadcast the pack current, so it is worked out from the BMS's energy counter. "
              + "That is less precise, but still finds a group that stands out."
            : null;
    }

    private void UpdateLiveGroups(BatteryData data, ChargeTestResult? result)
    {
        var voltages = data.BrickVoltages;
        HasLiveGroups = voltages.Count > 0;
        if (voltages.Count == 0)
        {
            LiveGroups.Clear();
            LiveSummary = "No cell group voltages yet.";
            return;
        }

        if (LiveGroups.Count != voltages.Count || !LiveGroups.Select(b => b.Index).SequenceEqual(voltages.Keys))
        {
            LiveGroups.Clear();
            foreach (var index in voltages.Keys)
                LiveGroups.Add(new GroupBar(index));
        }

        var low = voltages.Values.Min();
        var high = voltages.Values.Max();
        var span = Math.Max(high - low, 0.001);
        var findings = result?.Flagged.ToDictionary(g => g.Index, g => g.Finding) ?? [];
        foreach (var bar in LiveGroups)
        {
            var volts = voltages[bar.Index];
            bar.Height = BrickBar.MinHeight + (BrickBar.MaxHeight - BrickBar.MinHeight) * (volts - low) / span;
            bar.Level = Level(findings.GetValueOrDefault(bar.Index));
            bar.ToolTip = $"Cell group {bar.Index + 1}: {volts.ToString("0.0000", CultureInfo.CurrentCulture)} V";
        }

        var lowest = voltages.First(kv => kv.Value == low).Key + 1;
        var highest = voltages.First(kv => kv.Value == high).Key + 1;
        LiveSummary = $"Lowest {Display.Volts(low, "0.000")} (#{lowest}), highest {Display.Volts(high, "0.000")} (#{highest}), "
                      + $"{Display.Millivolts((high - low) * 1000)} apart. While charging, a group with higher resistance rises above the others.";
    }

    private void UpdateResult(ChargeTestResult? result)
    {
        HasResult = result is not null;
        if (result is null)
        {
            ResultKind = "none";
            ResultTitle = "No result yet";
            ResultDetail = "A result appears after the first measurement: the pack resting, then about a minute of charging (or the other way round).";
            Flagged.Clear();
            ResultGroups.Clear();
            PackResistanceText = AverageGroupText = MeasurementsText = ConditionsText = Display.Missing;
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
            ? "No group's voltage rises more than the others under charging current. Together with a small cell spread at rest, that is a healthy sign."
            : "Under the same current these groups' voltage rises more than the rest, which means higher internal resistance: usually an aged or damaged "
              + "group. The BMS protects its weakest group, so one weak group can make the car limit power and charging. Repeat the test another day "
              + "at a similar temperature; a group that stands out every time is worth showing to a service centre.";

        var flagged = result.Flagged.Take(8).Select(g => new FlaggedGroup(
            $"Cell group {g.Index + 1}",
            $"{ChargeTestResult.MilliOhm(g.ExcessMilliOhm, "0.00")} above the typical group"
            + (g.PercentAboveAverage is { } percent ? $" · about {percent.ToString("0", CultureInfo.CurrentCulture)}% higher than average" : ""),
            g.Finding == GroupFinding.MuchHigher ? "critical" : "attention")).ToList();
        if (!Flagged.SequenceEqual(flagged))
        {
            Flagged.Clear();
            foreach (var item in flagged)
                Flagged.Add(item);
        }

        UpdateResultGroups(result);
        PackResistanceText = ChargeTestResult.MilliOhm(result.PackResistanceMilliOhm, "0");
        AverageGroupText = ChargeTestResult.MilliOhm(result.AverageGroupMilliOhm, "0.00");
        MeasurementsText = result.StepsText;
        ConditionsText = result.Conditions;
    }

    private void UpdateResultGroups(ChargeTestResult result)
    {
        if (ResultGroups.Count != result.Groups.Count || !ResultGroups.Select(b => b.Index).SequenceEqual(result.Groups.Select(g => g.Index)))
        {
            ResultGroups.Clear();
            foreach (var group in result.Groups)
                ResultGroups.Add(new GroupBar(group.Index));
        }

        // At least 0.1 mΩ of scale, so ordinary variation between groups does not look dramatic.
        var low = result.Groups.Min(g => g.ExcessMilliOhm);
        var high = result.Groups.Max(g => g.ExcessMilliOhm);
        if (high - low < 0.1)
        {
            var middle = (high + low) / 2;
            (low, high) = (middle - 0.05, middle + 0.05);
        }

        foreach (var (bar, group) in ResultGroups.Zip(result.Groups))
        {
            bar.Height = BrickBar.MinHeight + (BrickBar.MaxHeight - BrickBar.MinHeight) * (group.ExcessMilliOhm - low) / (high - low);
            bar.Level = Level(group.Finding);
            bar.ToolTip = $"Cell group {group.Index + 1}: {(group.ExcessMilliOhm >= 0 ? "+" : "")}{ChargeTestResult.MilliOhm(group.ExcessMilliOhm, "0.000")} compared with the typical group"
                          + (group.PercentAboveAverage is { } percent ? $" ({percent.ToString("+0;-0", CultureInfo.CurrentCulture)}% of average)" : "");
        }
    }

    private static string Level(GroupFinding finding) => finding switch
    {
        GroupFinding.MuchHigher => "much-higher",
        GroupFinding.Higher => "higher",
        _ => "normal",
    };
}

/// <summary>One cell group in the charging test's charts.</summary>
public sealed partial class GroupBar(int index) : ObservableObject
{
    public int Index { get; } = index;

    [ObservableProperty]
    private double _height = BrickBar.MinHeight;

    /// <summary>"normal", "higher" or "much-higher".</summary>
    [ObservableProperty]
    private string _level = "normal";

    [ObservableProperty]
    private string _toolTip = "";
}

/// <summary>A cell group the charging test flagged.</summary>
/// <param name="Kind">"attention" or "critical".</param>
public sealed record FlaggedGroup(string Title, string Detail, string Kind);

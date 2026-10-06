using System.Globalization;

namespace OpenTeslaBuyer.Core.Battery;

/// <summary>What the pack is doing, as the charging test sees it.</summary>
public enum PackActivity
{
    /// <summary>No current reading yet.</summary>
    Unknown,
    Resting,
    Charging,

    /// <summary>Supplying more than a parked car uses (for example while driving); the test ignores this.</summary>
    Discharging,
}

/// <summary>How a cell group's resistance compares with the rest of the pack.</summary>
public enum GroupFinding
{
    Normal,
    Higher,
    MuchHigher,
}

/// <summary>One cell group in a charging test result.</summary>
/// <param name="Index">Zero-based, like <see cref="BatteryData.BrickVoltages"/>.</param>
/// <param name="ExcessMilliOhm">Resistance above the pack's typical group; negative means below.</param>
/// <param name="PercentAboveAverage">The excess as a share of the average group's resistance, when the pack's resistance is known.</param>
public sealed record GroupResistance(int Index, double ExcessMilliOhm, double? PercentAboveAverage, GroupFinding Finding);

/// <summary>One comparison of the pack at rest with the pack charging (in either order).</summary>
public sealed record ChargeStep(DateTimeOffset At, double RestAmps, double ChargingAmps)
{
    public double StepAmps => Math.Abs(ChargingAmps - RestAmps);
}

/// <summary>The outcome of a charging test: every cell group's resistance relative to the others.</summary>
public sealed record ChargeTestResult(
    IReadOnlyList<ChargeStep> Steps,
    IReadOnlyList<GroupResistance> Groups,
    double? PackResistanceMilliOhm,
    double? AverageGroupMilliOhm,
    bool CurrentEstimated,
    double? TempMinC,
    double? TempMaxC,
    double? SocPercent)
{
    /// <summary>Groups that stand out, highest resistance first.</summary>
    public IReadOnlyList<GroupResistance> Flagged { get; } =
        Groups.Where(g => g.Finding != GroupFinding.Normal).OrderByDescending(g => g.ExcessMilliOhm).ToList();

    public GroupFinding Worst => Flagged.Count == 0 ? GroupFinding.Normal : Flagged.Max(g => g.Finding);

    public string Summary => Flagged.Count switch
    {
        0 => $"All {Groups.Count} cell groups behave alike: none stands out under charging current.",
        1 => $"Cell group {Flagged[0].Index + 1} has {(Flagged[0].Finding == GroupFinding.MuchHigher ? "much " : "")}higher resistance than the others.",
        _ => $"{Flagged.Count} cell groups have higher resistance than the others: "
             + string.Join(", ", Flagged.Take(6).Select(g => (g.Index + 1).ToString(CultureInfo.CurrentCulture)))
             + (Flagged.Count > 6 ? "…" : "") + ".",
    };

    /// <summary>E.g. "Cell group 57: 0.20 mΩ above the typical group, about 31% higher than average".</summary>
    public static string Describe(GroupResistance group) =>
        $"Cell group {group.Index + 1}: {MilliOhm(group.ExcessMilliOhm, "0.00")} above the typical group"
        + (group.PercentAboveAverage is { } percent ? $", about {percent.ToString("0", CultureInfo.CurrentCulture)}% higher than average" : "");

    public string Conditions
    {
        get
        {
            var parts = new List<string>();
            if (TempMinC is { } low && TempMaxC is { } high)
                parts.Add($"pack {Display.Number(low, "0")}–{Display.Number(high, "0")} °C");
            else if ((TempMaxC ?? TempMinC) is { } temperature)
                parts.Add($"pack {Display.Number(temperature, "0")} °C");
            if (SocPercent is not null)
                parts.Add($"{Display.Percent(SocPercent, "0")} charged");
            return parts.Count == 0 ? Display.Missing : string.Join(", ", parts);
        }
    }

    /// <summary>E.g. "2 measurements, steps up to 252 A".</summary>
    public string StepsText =>
        $"{Measurements(Steps.Count)}, steps up to {Steps.Max(s => s.StepAmps).ToString("0", CultureInfo.CurrentCulture)} A{(CurrentEstimated ? " (estimated)" : "")}";

    public string ToText()
    {
        var lines = new List<string> { $"Charging test: {Summary}" };
        lines.AddRange(Flagged.Take(10).Select(g => "  " + Describe(g)));
        if (PackResistanceMilliOhm is { } pack)
            lines.Add($"  Pack resistance about {MilliOhm(pack, "0")} ({MilliOhm(AverageGroupMilliOhm, "0.00")} per cell group on average)");
        lines.Add($"  {StepsText}; {Conditions}");
        return string.Join(Environment.NewLine, lines);
    }

    public static string MilliOhm(double? value, string format) => value is { } v ? v.ToString(format, CultureInfo.CurrentCulture) + " mΩ" : Display.Missing;

    public static string Measurements(int count) => count == 1 ? "1 measurement" : $"{count} measurements";
}

/// <summary>The charging test as it stands, for display.</summary>
/// <param name="Amps">Positive into the pack.</param>
/// <param name="PowerKw">Positive into the pack.</param>
/// <param name="Progress">What the test is doing or waiting for, as a sentence.</param>
public sealed record ChargeTestStatus(
    PackActivity Activity,
    double? Amps,
    double? PowerKw,
    bool CurrentEstimated,
    TimeSpan ActivityDuration,
    double? EnergyAddedKWh,
    string Progress,
    ChargeTestResult? Result);

/// <summary>
/// Finds cell groups with higher internal resistance by comparing every group at rest with the same group while charging.
/// Each sample takes every group's deviation from the pack's median group; when the current changes by ΔI, a group's
/// deviation changes by ΔI × (its resistance − the typical group's). State of charge and temperature move all groups
/// together, so they cancel out. Each measurement pairs the last stretch of one state with the first stretch of the next
/// (resting then charging, or charging then resting), and several measurements are averaged.
/// </summary>
/// <remarks>
/// Feed it after every decoded frame; it samples four times per second of the source's own clock, so recordings
/// replay to the same result. Where the car does not broadcast the pack current (2012–2021 Model S/X), the current is
/// estimated from the BMS's lifetime energy counters, which lag by several seconds, so samples near a change are skipped.
/// </remarks>
public sealed class ChargeTest
{
    internal static readonly TimeSpan SampleInterval = TimeSpan.FromMilliseconds(250);

    /// <summary>Skipped after the pack changes state: the cell-voltage message takes a few seconds to cover every group.</summary>
    internal static readonly TimeSpan SettleTime = TimeSpan.FromSeconds(10);

    /// <summary>How much of each state a measurement uses on either side of the change.</summary>
    internal static readonly TimeSpan WindowLength = TimeSpan.FromSeconds(45);

    internal static readonly TimeSpan MinimumWindow = TimeSpan.FromSeconds(20);

    /// <summary>Resting and charging further apart than this are not compared.</summary>
    internal static readonly TimeSpan MaximumGap = TimeSpan.FromMinutes(2);

    /// <summary>The span the energy counters are differentiated over.</summary>
    internal static readonly TimeSpan RateWindow = TimeSpan.FromSeconds(10);

    /// <summary>How far an energy-counter estimate of the current trails the real current.</summary>
    internal static readonly TimeSpan EstimateLag = TimeSpan.FromSeconds(12);

    internal const double ChargingAmps = 5;
    internal const double RestingChargeAmps = 2;

    /// <summary>A parked car with the climate running draws up to about this much; beyond it the pack counts as discharging.</summary>
    internal const double RestingDischargeAmps = 25;

    internal const double MinimumStepAmps = 8;
    internal const int MinimumGroups = 8;

    /// <summary>A group is flagged when it is this share of the average group's resistance above the typical group...</summary>
    internal const double HigherShare = 0.15;
    internal const double MuchHigherShare = 0.35;

    /// <summary>...or, when the pack's own resistance is unknown, this many milliohms above it.</summary>
    internal const double HigherMilliOhm = 0.08;
    internal const double MuchHigherMilliOhm = 0.2;

    private readonly List<ChargeStep> _steps = [];
    private readonly SortedDictionary<int, (double Sum, double Weight)> _groups = [];
    private readonly Queue<CounterReading> _counters = new();
    private (double Sum, double Weight) _pack;
    private DateTimeOffset? _lastSample;
    private Segment? _segment;
    private Segment? _previous;
    private double? _firstChargeKWh;
    private double? _lastChargeKWh;
    private int _orientation = 1;
    private bool _estimated;
    private bool _hasGroups;
    private double? _amps;
    private double? _powerKw;
    private double? _soc;
    private double? _tempMin;
    private double? _tempMax;

    public void Reset()
    {
        _steps.Clear();
        _groups.Clear();
        _counters.Clear();
        _pack = default;
        _lastSample = null;
        _segment = null;
        _previous = null;
        _firstChargeKWh = null;
        _lastChargeKWh = null;
        _orientation = 1;
        _estimated = false;
        _hasGroups = false;
        _amps = null;
        _powerKw = null;
    }

    /// <param name="time">The frame's own timestamp.</param>
    public void Observe(BatteryData data, DateTimeOffset time)
    {
        if (_lastSample is { } last)
        {
            if (time < last - TimeSpan.FromSeconds(1))
                Reset(); // the source started over, e.g. a replay
            else if (time - last < SampleInterval)
                return;
        }

        _lastSample = time;
        _soc = data.SocUiPercent;
        _tempMin = data.TempMinC;
        _tempMax = data.TempMaxC;
        _firstChargeKWh ??= data.ChargeTotalKWh;
        _lastChargeKWh = data.ChargeTotalKWh ?? _lastChargeKWh;

        _counters.Enqueue(new CounterReading(time, data.ChargeTotalKWh, data.DischargeTotalKWh, data.PackCurrent));
        while (time - _counters.Peek().Time > RateWindow)
            _counters.Dequeue();

        var (chargeKw, dischargeKw, meanRawAmps) = CounterRates();
        if (chargeKw >= 1 && (dischargeKw ?? 0) < 0.3 * chargeKw && meanRawAmps is { } raw && Math.Abs(raw) >= ChargingAmps)
            _orientation = raw > 0 ? 1 : -1; // the energy counters settle which sign of the current means charging

        _estimated = data.PackCurrent is null;
        if (data.PackCurrent is { } current)
        {
            _amps = _orientation * current;
            _powerKw = data.PackVoltage is { } volts ? _amps * volts / 1000 : null;
        }
        else if (chargeKw is { } inKw && dischargeKw is { } outKw && data.PackVoltage is { } volts && volts > 50)
        {
            _powerKw = inKw - outKw;
            _amps = _powerKw * 1000 / volts;
        }
        else
        {
            _amps = null;
            _powerKw = null;
        }

        var activity = Classify(_amps, _segment?.Activity ?? PackActivity.Unknown);
        if (_segment is null || activity != _segment.Activity)
            StartSegment(activity, time);

        var segment = _segment!;
        segment.End = time;
        if (!segment.Measured && time - segment.SettledFrom > WindowLength && Length(segment.Head) >= MinimumWindow)
            Measure(segment);

        var deviations = Deviations(data.BrickVoltages);
        _hasGroups = deviations is not null;
        if (deviations is null || _amps is not { } amps || time < segment.SettledFrom)
            return;

        var sample = new Sample(time, amps, data.PackVoltage, deviations);
        if (time - segment.SettledFrom <= WindowLength)
            segment.Head.Add(sample);
        segment.Tail.Enqueue(sample);
        while (time - segment.Tail.Peek().Time > WindowLength + EstimateLag)
            segment.Tail.Dequeue();
    }

    public ChargeTestStatus Status()
    {
        var result = Result();
        return new ChargeTestStatus(
            _segment?.Activity ?? PackActivity.Unknown,
            _amps,
            _powerKw,
            _estimated,
            _segment is null ? TimeSpan.Zero : _segment.End - _segment.Start,
            _firstChargeKWh is { } first && _lastChargeKWh is { } latest ? latest - first : null,
            Progress(result),
            result);
    }

    private static PackActivity Classify(double? amps, PackActivity current) => amps switch
    {
        null => PackActivity.Unknown,
        >= ChargingAmps => PackActivity.Charging,
        >= -RestingDischargeAmps and <= RestingChargeAmps => PackActivity.Resting,
        < -RestingDischargeAmps => PackActivity.Discharging,
        _ => current is PackActivity.Charging or PackActivity.Resting ? current : PackActivity.Unknown, // between the two thresholds
    };

    private void StartSegment(PackActivity activity, DateTimeOffset time)
    {
        if (_segment is { } ended)
        {
            // A state that ended before a full window still counts if it lasted long enough.
            if (!ended.Measured && Length(ended.Head) >= MinimumWindow)
                Measure(ended);

            if (ended.Activity is PackActivity.Resting or PackActivity.Charging && Length(UsableTail(ended)) >= MinimumWindow)
                _previous = ended;
            else if (ended.End - ended.Start > MaximumGap)
                _previous = null; // e.g. a drive in between: too much has changed to compare across it
        }

        _segment = new Segment(activity, time, SettleTime + (_estimated ? EstimateLag : TimeSpan.Zero));
    }

    private void Measure(Segment segment)
    {
        segment.Measured = true;
        if (_previous is not { } previous
            || previous.Activity == segment.Activity
            || segment.Activity is not (PackActivity.Resting or PackActivity.Charging)
            || segment.Start - previous.End > MaximumGap)
            return;

        var before = UsableTail(previous);
        var after = segment.Head;
        if (Length(before) < MinimumWindow || Length(after) < MinimumWindow)
            return;

        var ampsBefore = before.Average(s => s.Amps);
        var ampsAfter = after.Average(s => s.Amps);
        var step = ampsAfter - ampsBefore;
        if (Math.Abs(step) < MinimumStepAmps)
            return;

        var weight = Math.Abs(step);
        var groupCount = Math.Max(before.Max(s => s.Deviations.Length), after.Max(s => s.Deviations.Length));
        for (var group = 0; group < groupCount; group++)
        {
            if (Mean(before, s => At(s.Deviations, group)) is not { } devBefore || Mean(after, s => At(s.Deviations, group)) is not { } devAfter)
                continue;

            var ohm = (devAfter - devBefore) / step;
            var (sum, total) = _groups.GetValueOrDefault(group);
            _groups[group] = (sum + ohm * weight, total + weight);
        }

        if (Mean(before, s => s.PackVolts) is { } voltsBefore && Mean(after, s => s.PackVolts) is { } voltsAfter
            && (voltsAfter - voltsBefore) / step is var packOhm and >= 0.005 and <= 1)
            _pack = (_pack.Sum + packOhm * weight, _pack.Weight + weight);

        var charging = segment.Activity == PackActivity.Charging;
        _steps.Add(new ChargeStep(after[^1].Time, charging ? ampsBefore : ampsAfter, charging ? ampsAfter : ampsBefore));
    }

    /// <summary>The last window of a finished state, minus the part an estimated current may have mislabelled.</summary>
    private IReadOnlyList<Sample> UsableTail(Segment segment)
    {
        var lag = _estimated ? EstimateLag : TimeSpan.Zero;
        var usable = segment.Tail.Where(s => s.Time <= segment.End - lag).ToList();
        return usable.Count == 0 ? usable : usable.Where(s => s.Time >= usable[^1].Time - WindowLength).ToList();
    }

    private ChargeTestResult? Result()
    {
        if (_steps.Count == 0 || _groups.Count < MinimumGroups)
            return null;

        var groups = _groups.Select(g => (Index: g.Key, MilliOhm: g.Value.Sum / g.Value.Weight * 1000)).ToList();
        var typical = Median(groups.Select(g => g.MilliOhm));
        var sigma = Math.Max(1.4826 * Median(groups.Select(g => Math.Abs(g.MilliOhm - typical))), 0.005);
        double? pack = _pack.Weight > 0 ? _pack.Sum / _pack.Weight * 1000 : null;
        var average = pack / groups.Count;
        var higher = average * HigherShare ?? HigherMilliOhm;
        var muchHigher = average * MuchHigherShare ?? MuchHigherMilliOhm;

        var results = groups.Select(g =>
        {
            var excess = g.MilliOhm - typical;
            var finding = excess >= Math.Max(6 * sigma, muchHigher) ? GroupFinding.MuchHigher
                : excess >= Math.Max(4 * sigma, higher) ? GroupFinding.Higher
                : GroupFinding.Normal;
            return new GroupResistance(g.Index, excess, average is > 0 ? excess / average * 100 : null, finding);
        }).ToList();

        return new ChargeTestResult(_steps.ToList(), results, pack, average, _estimated, _tempMin, _tempMax, _soc);
    }

    private string Progress(ChargeTestResult? result)
    {
        if (_lastSample is null)
            return "Waiting for data from the car.";
        if (!_hasGroups)
            return "Waiting for the cell group voltages.";
        if (_amps is not { } amps || _segment is not { } segment)
        {
            return _estimated
                ? "Working out the current from the BMS energy counters (about 10 seconds)."
                : "Waiting for the pack current.";
        }

        var done = result is null ? "" : $" {ChargeTestResult.Measurements(result.Steps.Count)} so far.";
        var settling = segment.End < segment.SettledFrom;
        var paired = _previous is { } previous && previous.Activity != segment.Activity && segment.Start - previous.End <= MaximumGap;
        var head = $"{Seconds(Length(segment.Head))} of {Seconds(WindowLength)} s";
        return segment.Activity switch
        {
            PackActivity.Discharging =>
                $"The pack is supplying {(-amps).ToString("0", CultureInfo.CurrentCulture)} A. The test needs the car parked, resting or charging.{done}",
            PackActivity.Resting when paired && !segment.Measured =>
                settling ? "Charging stopped. Letting the voltages settle…" : $"Charging stopped. Measuring at rest: {head}.",
            PackActivity.Resting when Length(UsableTail(segment)) >= MinimumWindow =>
                $"Ready. Start charging: the first minute of charging completes a measurement.{done}",
            PackActivity.Resting =>
                $"Resting. Recording the baseline: {Seconds(Length(UsableTail(segment)))} of {Seconds(MinimumWindow)} s.{done}",
            PackActivity.Charging when paired && !segment.Measured =>
                settling ? "Charging started. Letting the voltages settle…" : $"Measuring while charging: {head}.",
            PackActivity.Charging when result is not null =>
                $"Measured.{done} To add another measurement, stop charging for about a minute while staying connected.",
            PackActivity.Charging =>
                "Charging. To measure, stop charging for about a minute while staying connected, or connect before the next charge starts.",
            _ => "Waiting for the pack to rest or charge.",
        };
    }

    private (double? ChargeKw, double? DischargeKw, double? MeanRawAmps) CounterRates()
    {
        var oldest = _counters.Peek();
        var newest = _counters.Last();
        var hours = (newest.Time - oldest.Time).TotalHours;
        if (hours < RateWindow.TotalHours * 0.8)
            return (null, null, null);

        double? Rate(double? from, double? to) => from is { } a && to is { } b && b >= a ? (b - a) / hours : null;
        var raw = _counters.Where(c => c.RawAmps is not null).Select(c => c.RawAmps!.Value).ToList();
        return (Rate(oldest.ChargeKWh, newest.ChargeKWh), Rate(oldest.DischargeKWh, newest.DischargeKWh), raw.Count == 0 ? null : raw.Average());
    }

    /// <summary>Each group's voltage minus the median group's, indexed by group; NaN where a group has no reading.</summary>
    private static double[]? Deviations(SortedDictionary<int, double> voltages)
    {
        if (voltages.Count < MinimumGroups)
            return null;

        var median = Median(voltages.Values);
        var deviations = new double[voltages.Keys.Last() + 1];
        Array.Fill(deviations, double.NaN);
        foreach (var (group, volts) in voltages)
            deviations[group] = volts - median;
        return deviations;
    }

    private static double At(double[] values, int index) => index < values.Length ? values[index] : double.NaN;

    /// <summary>The mean of the available values, or null when fewer than half the samples have one.</summary>
    private static double? Mean(IReadOnlyList<Sample> samples, Func<Sample, double?> value)
    {
        double sum = 0;
        var count = 0;
        foreach (var sample in samples)
        {
            if (value(sample) is { } v && !double.IsNaN(v))
            {
                sum += v;
                count++;
            }
        }

        return count * 2 >= samples.Count && count > 0 ? sum / count : null;
    }

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToArray();
        var middle = sorted.Length / 2;
        return sorted.Length % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    private static TimeSpan Length(IReadOnlyList<Sample> samples) => samples.Count < 2 ? TimeSpan.Zero : samples[^1].Time - samples[0].Time;

    private static string Seconds(TimeSpan span) => Math.Round(span.TotalSeconds).ToString("0", CultureInfo.CurrentCulture);

    private readonly record struct CounterReading(DateTimeOffset Time, double? ChargeKWh, double? DischargeKWh, double? RawAmps);

    private sealed record Sample(DateTimeOffset Time, double Amps, double? PackVolts, double[] Deviations);

    /// <summary>A stretch of time in one <see cref="PackActivity"/>.</summary>
    private sealed class Segment(PackActivity activity, DateTimeOffset start, TimeSpan settle)
    {
        public PackActivity Activity { get; } = activity;

        public DateTimeOffset Start { get; } = start;

        public DateTimeOffset SettledFrom { get; } = start + settle;

        public DateTimeOffset End { get; set; } = start;

        /// <summary>The first window after settling.</summary>
        public List<Sample> Head { get; } = [];

        /// <summary>The latest samples, a little more than a window's worth.</summary>
        public Queue<Sample> Tail { get; } = new();

        public bool Measured { get; set; }
    }
}

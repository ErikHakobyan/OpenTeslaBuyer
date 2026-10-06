using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Tests;

public class ChargeTestTests
{
    private const int Groups = 96;
    private const int Weak = 40;
    private const double TypicalMilliOhm = 0.50;
    private const double WeakExtraMilliOhm = 0.25;
    private const double ConnectionOhm = 0.012;
    private static readonly DateTimeOffset Start = new(2026, 10, 6, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void FindsTheWeakGroupWhenChargingStartsAndStops()
    {
        var status = Run(new ChargeTest(), new Pack(), [(60, -2), (90, 250), (70, -2)]);

        var result = Assert.IsType<ChargeTestResult>(status.Result);
        Assert.Equal(2, result.Steps.Count);
        var flagged = Assert.Single(result.Flagged);
        Assert.Equal(Weak, flagged.Index);
        Assert.InRange(flagged.ExcessMilliOhm, WeakExtraMilliOhm * 0.85, WeakExtraMilliOhm * 1.15);
        Assert.False(result.CurrentEstimated);

        // 96 groups of about 0.5 mΩ plus the connections; charging raises the open-circuit voltage a little meanwhile.
        var expectedPack = Groups * TypicalMilliOhm + WeakExtraMilliOhm + ConnectionOhm * 1000;
        Assert.InRange(result.PackResistanceMilliOhm!.Value, expectedPack * 0.85, expectedPack * 1.15);
        Assert.Contains($"Cell group {Weak + 1}", result.Summary);
        Assert.Equal(PackActivity.Resting, status.Activity);
    }

    [Fact]
    public void EstimatesTheCurrentFromTheEnergyCountersWhenItIsNotBroadcast()
    {
        // A 2012–2021 Model S/X on a home charger: no current signal, so it comes from the BMS's lifetime energy counters.
        var status = Run(new ChargeTest(), new Pack(), [(70, -1.5), (120, 29), (80, -1.5)], broadcastCurrent: false);

        var result = Assert.IsType<ChargeTestResult>(status.Result);
        Assert.True(result.CurrentEstimated);
        Assert.Equal(2, result.Steps.Count);
        Assert.All(result.Steps, step => Assert.InRange(step.StepAmps, 27, 34));
        var flagged = Assert.Single(result.Flagged);
        Assert.Equal(Weak, flagged.Index);
        Assert.InRange(flagged.ExcessMilliOhm, WeakExtraMilliOhm * 0.75, WeakExtraMilliOhm * 1.25);
    }

    [Fact]
    public void WorksWhenTheCarReportsChargingCurrentAsNegative()
    {
        var status = Run(new ChargeTest(), new Pack(), [(60, -2), (90, 250), (70, -2)], sign: -1);

        var result = Assert.IsType<ChargeTestResult>(status.Result);
        Assert.Equal(Weak, Assert.Single(result.Flagged).Index);
        Assert.All(result.Steps, step => Assert.True(step.ChargingAmps > 200));
    }

    [Fact]
    public void NeedsTheCarToRestNextToCharging()
    {
        var status = Run(new ChargeTest(), new Pack(), [(200, 250)]);

        Assert.Null(status.Result);
        Assert.Equal(PackActivity.Charging, status.Activity);
        Assert.Contains("stop charging", status.Progress);
    }

    [Fact]
    public void IgnoresDischarging()
    {
        var test = new ChargeTest();
        var pack = new Pack();

        var during = Run(test, pack, [(60, -2), (60, -150)]);
        Assert.Equal(PackActivity.Discharging, during.Activity);
        Assert.Contains("needs the car parked", during.Progress);

        var after = Run(test, pack, [(70, -2)], from: Start.AddSeconds(120));
        Assert.Null(after.Result);
    }

    [Fact]
    public void ReportsProgressWhileWaitingForTheBaseline()
    {
        var test = new ChargeTest();
        Assert.Equal("Waiting for data from the car.", test.Status().Progress);

        var resting = Run(test, new Pack(), [(15, -2)]);
        Assert.Equal(PackActivity.Resting, resting.Activity);
        Assert.StartsWith("Resting.", resting.Progress);

        var ready = Run(test, new Pack(), [(25, -2)], from: Start.AddSeconds(15));
        Assert.StartsWith("Ready.", ready.Progress);
    }

    [Fact]
    public void StartsOverWhenTheSourceDoes()
    {
        var test = new ChargeTest();
        Run(test, new Pack(), [(60, -2), (90, 250), (70, -2)]);
        Assert.NotNull(test.Status().Result);

        // A replay that starts again goes back in time.
        var again = Run(test, new Pack(), [(5, -2)], from: Start);
        Assert.Null(again.Result);
    }

    [Theory]
    [InlineData(SimulatedCar.Model3Charging, false)]
    [InlineData(SimulatedCar.ModelSCharging, true)]
    public async Task FindsTheSimulatorsWeakCellGroup(SimulatedCar car, bool estimated)
    {
        var monitor = new BatteryMonitor(new AutoDetectProfile());
        using var cts = new CancellationTokenSource();
        DateTimeOffset? first = null;
        await foreach (var frame in new SimulatorAdapter(car, TimeSpan.Zero).StreamAsync(() => monitor.Profile.MonitoredIds, _ => { }, cts.Token))
        {
            monitor.Process(frame);
            first ??= frame.Timestamp;
            if (frame.Timestamp - first > TimeSpan.FromSeconds(260))
                break;
        }

        var status = monitor.ChargeTestStatus();
        var result = Assert.IsType<ChargeTestResult>(status.Result);
        Assert.Equal(estimated, result.CurrentEstimated);
        Assert.Equal(2, result.Steps.Count);
        var flagged = Assert.Single(result.Flagged);
        Assert.Equal(SimulatorAdapter.ChargingValues.WeakGroup, flagged.Index);
        Assert.Equal(PackActivity.Resting, status.Activity);
        Assert.True(status.EnergyAddedKWh > 0);
    }

    [Fact]
    public async Task TheParkedSimulatorProducesNoResult()
    {
        var monitor = new BatteryMonitor(new AutoDetectProfile());
        var frames = new SimulatorAdapter(SimulatedCar.Model3, TimeSpan.Zero)
            .StreamAsync(() => monitor.Profile.MonitoredIds, _ => { }, CancellationToken.None);
        foreach (var frame in await AdapterTests.Take(frames, 2000))
            monitor.Process(frame);

        var status = monitor.ChargeTestStatus();
        Assert.Null(status.Result);
        Assert.NotEqual(PackActivity.Charging, status.Activity);
    }

    [Fact]
    public void TheReportIncludesTheResult()
    {
        var result = Run(new ChargeTest(), new Pack(), [(60, -2), (90, 250), (70, -2)]).Result!;
        var data = new BatteryData();
        var health = HealthCalculator.Evaluate(data);

        var html = ReportWriter.ToHtml(data, health, false, Start, chargeTest: result);
        Assert.Contains("<h2>Charging test</h2>", html);
        Assert.Contains($"Cell group {Weak + 1}:", html);

        var text = ReportWriter.ToText(data, health, false, chargeTest: result);
        Assert.Contains("Charging test: Cell group 41 has", text);
        Assert.Contains("2 measurements", text);
    }

    /// <summary>Feeds a schedule of (seconds, amps into the pack) to the test, four samples per second.</summary>
    private static ChargeTestStatus Run(
        ChargeTest test, Pack pack, (double Seconds, double Amps)[] schedule, bool broadcastCurrent = true, int sign = 1, DateTimeOffset? from = null)
    {
        var time = from ?? Start;
        foreach (var (seconds, amps) in schedule)
        {
            for (var elapsed = 0.0; elapsed < seconds; elapsed += 0.25)
            {
                var snapshot = pack.Snapshot(amps, broadcastCurrent ? sign * amps : null);
                test.Observe(snapshot, time);
                pack.Advance(amps, 0.25, snapshot.PackVoltage!.Value);
                time += TimeSpan.FromMilliseconds(250);
            }
        }

        return test.Status();
    }

    /// <summary>96 cell groups of about 0.5 mΩ, one of them 0.25 mΩ higher; open-circuit voltage follows the charge put in.</summary>
    private sealed class Pack
    {
        private readonly double[] _resting = Enumerable.Range(0, Groups).Select(g => 3.80 + 0.002 * Math.Sin(g * 0.9)).ToArray();
        private readonly double[] _ohms = Enumerable.Range(0, Groups)
            .Select(g => (TypicalMilliOhm * (1 + 0.03 * Math.Cos(g * 1.3)) + (g == Weak ? WeakExtraMilliOhm : 0)) / 1000)
            .ToArray();

        private double _charged = 41_000;
        private double _discharged = 39_000;
        private double _soc = 50;

        public BatteryData Snapshot(double amps, double? reportedAmps)
        {
            var data = new BatteryData
            {
                PackCurrent = reportedAmps,
                SocUiPercent = Math.Round(_soc, 1),
                TempMinC = 20,
                TempMaxC = 22,

                // Like the real counters: 1 Wh steps.
                ChargeTotalKWh = Math.Floor(_charged * 1000) / 1000,
                DischargeTotalKWh = Math.Floor(_discharged * 1000) / 1000,
            };

            var shift = (_soc - 50) * 0.0072;
            double pack = 0;
            for (var group = 0; group < Groups; group++)
            {
                var volts = _resting[group] + shift + amps * _ohms[group];
                data.BrickVoltages[group] = Math.Round(volts, 4); // 0.1 mV, as the Model 3 reports them
                pack += volts;
            }

            data.PackVoltage = Math.Round(pack + amps * ConnectionOhm, 2);
            return data;
        }

        public void Advance(double amps, double seconds, double packVolts)
        {
            var kwh = packVolts * amps * seconds / 3_600_000;
            if (kwh > 0)
                _charged += kwh;
            else
                _discharged -= kwh;
            _soc += kwh / 70 * 100;
        }
    }
}

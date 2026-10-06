using Microsoft.Data.Sqlite;
using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Buyer;
using OpenTeslaBuyer.Core.Can;
using OpenTeslaBuyer.Core.Storage;
using OpenTeslaBuyer.Core.Vehicles;
using M3 = OpenTeslaBuyer.Core.Vehicles.Model3Signals;
using SX = OpenTeslaBuyer.Core.Vehicles.ModelSXSignals;

namespace OpenTeslaBuyer.Core.Tests;

public class ParkedDiagnosticsTests
{
    private static readonly DateTimeOffset Evening = new(2026, 10, 5, 20, 0, 0, TimeSpan.Zero);

    // ---------------------------------------------------------------- decoding

    [Fact]
    public void DecodesTheChargerAndDcDcMessages()
    {
        var data = new BatteryData();
        var profile = new Model3Profile();
        profile.Process(Frame(M3.ChargeLine, d =>
        {
            M3.AcVoltage.Encode(d, 231);
            M3.AcCurrent.Encode(d, 15.8);
            M3.AcInputPower.Encode(d, 10.9);
            M3.AcCurrentLimit.Encode(d, 16);
        }), data);
        profile.Process(Frame(M3.ChargerStatus, d =>
        {
            M3.ChargerMainState.EncodeRaw(d, 6);
            M3.ChargerHvStatus.EncodeRaw(d, 2);
            M3.GridConfig.EncodeRaw(d, 3); // three-phase delta: all ones, which must not read as "not available"
            M3.PhaseEnabled[0].EncodeRaw(d, 1);
            M3.PhaseEnabled[2].EncodeRaw(d, 1);
            M3.ChargerVariant.EncodeRaw(d, 2);
        }), data);
        profile.Process(Frame(M3.DcDcRail, d =>
        {
            M3.DcDcLowVoltage.Encode(d, 14.1);
            M3.DcDcOutputCurrent.Encode(d, 27.5);
        }), data);
        profile.Process(Frame(M3.DcDcStatus, d =>
        {
            M3.DcDcMainState.EncodeRaw(d, 1);
            M3.DcDcFaulted.EncodeRaw(d, 1);
        }), data);

        Assert.Equal(231, data.AcVolts!.Value, 0.1);
        Assert.Equal(15.8, data.AcAmps!.Value, 3);
        Assert.Equal(10.9, data.AcInputKw!.Value, 3);
        Assert.Equal(16, data.AcCurrentLimitAmps!.Value, 3);
        Assert.Equal(6, data.ChargerState);
        Assert.Equal(2, data.ChargerHvStatus);
        Assert.Equal(3, data.GridConfig);
        Assert.Equal(0b101, data.ChargerPhases);
        Assert.Equal(2, data.ChargerVariant);
        Assert.Equal(14.1, data.DcDcVolts!.Value, 1);
        Assert.Equal(27.5, data.DcDcAmps!.Value, 3);
        Assert.Equal(1, data.DcDcState);
        Assert.True(data.DcDcFaulted);
    }

    [Fact]
    public void DecodesTheModelSDcDcConverter()
    {
        var data = new BatteryData();
        new ModelSXProfile().Process(Frame(SX.DcDc, d =>
        {
            SX.DcDcOutputVoltage.Encode(d, 13.8);
            SX.DcDcOutputCurrent.Encode(d, 21);
        }, length: 7), data);

        Assert.Equal(13.8, data.DcDcVolts!.Value, 3);
        Assert.Equal(21, data.DcDcAmps!.Value, 3);
    }

    // ---------------------------------------------------------------- overnight test

    [Fact]
    public void FlagsAGroupThatLostChargeOvernight()
    {
        var evening = Snapshot(Evening, g => 3.861 + 0.001 * Math.Sin(g));
        var morning = Snapshot(Evening.AddHours(10), g => 3.858 + 0.001 * Math.Sin(g) - (g == 20 ? 0.009 : 0));

        var result = CellDrift.Compare(evening, morning)!;

        var flagged = Assert.Single(result.Flagged);
        Assert.Equal(20, flagged.Index);
        Assert.Equal(GroupFinding.MuchHigher, flagged.Finding);
        Assert.Equal(-9, flagged.ChangeMv, 0.5);
        Assert.Equal(-21.6, flagged.MvPerDay, 1);
        Assert.Contains("Cell group 21 lost more charge", result.Summary);
        Assert.False(result.FlatVoltagePack);
    }

    [Fact]
    public void BalancingTheHighestGroupsIsNotALeak()
    {
        // The BMS bleeds the highest groups down: they drop, but stay above the others.
        var evening = Snapshot(Evening, g => 3.861 + (g < 3 ? 0.012 : 0));
        var morning = Snapshot(Evening.AddHours(10), g => 3.861 + (g < 3 ? 0.002 : 0));

        Assert.Empty(CellDrift.Compare(evening, morning)!.Flagged);
    }

    [Fact]
    public void NeedsTheReadingsHoursApart()
    {
        var evening = Snapshot(Evening, _ => 3.86);
        Assert.Null(CellDrift.Compare(evening, Snapshot(Evening.AddHours(1), _ => 3.86)));
        Assert.Null(CellDrift.Compare(evening, Snapshot(Evening.AddDays(40), _ => 3.86)));
        Assert.NotNull(CellDrift.Compare(evening, Snapshot(Evening.AddHours(5), _ => 3.86)));
    }

    [Fact]
    public void RecognisesAFlatVoltagePack()
    {
        var result = CellDrift.Compare(Snapshot(Evening, _ => 3.29), Snapshot(Evening.AddHours(9), _ => 3.29))!;
        Assert.True(result.FlatVoltagePack);
    }

    [Fact]
    public void TakesAParkedSnapshotOnlyBeforeCharging()
    {
        var resting = new ChargeTest();
        Feed(resting, seconds: 50, amps: -1.5);
        var snapshot = resting.Status().ParkedSnapshot;
        Assert.NotNull(snapshot);
        Assert.Equal(96, snapshot.Volts.Count);
        Assert.Equal(3.861, snapshot.MedianVolts, 3);

        var charging = new ChargeTest();
        Feed(charging, seconds: 60, amps: 30);
        Feed(charging, seconds: 60, amps: -1.5, from: Evening.AddSeconds(60));
        Assert.Null(charging.Status().ParkedSnapshot); // the cells are still settling after the charge
    }

    // ---------------------------------------------------------------- charger

    [Fact]
    public void FindsAMissingPhase()
    {
        var report = Charger(new BatteryData
        {
            AcVolts = 230, AcAmps = 15.9, AcInputKw = 7.3, AcCurrentLimitAmps = 16, ChargerState = 6, ChargerHvStatus = 2,
            GridConfig = 2, ChargerPhases = 0b011, ChargerVariant = 2, PackVoltage = 380, SocUiPercent = 50,
        }, packAmps: 17.3);

        Assert.Equal(CheckStatus.Attention, Item(report, "chargerPhases").Status);
        Assert.Contains("2 of 3 phases", Item(report, "chargerPhases").Summary);
        Assert.Equal(CheckStatus.Pass, Item(report, "chargerCurrent").Status);
        Assert.Equal(90, report.EfficiencyPercent!.Value, 0);
        Assert.Equal(CheckStatus.Pass, Item(report, "chargerEfficiency").Status);
        Assert.Equal("Three-phase charger", report.Charger);
        Assert.Equal(CheckStatus.Attention, report.Overall);
    }

    [Fact]
    public void FlagsLowCurrentAndLowEfficiency()
    {
        var report = Charger(new BatteryData
        {
            AcVolts = 240, AcAmps = 16, AcInputKw = 3.8, AcCurrentLimitAmps = 32, ChargerState = 6, ChargerHvStatus = 2,
            GridConfig = 1, ChargerPhases = 0b001, PackVoltage = 375, SocUiPercent = 40,
        }, packAmps: 7);

        Assert.Equal(CheckStatus.Pass, Item(report, "chargerPhases").Status);
        Assert.Equal(CheckStatus.Attention, Item(report, "chargerCurrent").Status);
        Assert.Contains("16.0 A of the 32 A", Item(report, "chargerCurrent").Summary);
        Assert.Equal(CheckStatus.Attention, Item(report, "chargerEfficiency").Status);
    }

    [Fact]
    public void ReportsAChargerFault()
    {
        var report = Charger(new BatteryData { AcVolts = 240, AcAmps = 0, ChargerState = 8, ChargerHvStatus = 3 }, packAmps: null);
        Assert.Equal(CheckStatus.Fail, Item(report, "chargerFaults").Status);
    }

    [Fact]
    public void WithoutAcChargingNothingIsJudged()
    {
        var report = Charger(new BatteryData { PackVoltage = 380 }, packAmps: -1);
        Assert.False(report.Measuring);
        Assert.Equal(CheckStatus.NotAvailable, report.Overall);
        Assert.Null(new SessionTests(Charger: report).ChargerIfMeasured);
    }

    // ---------------------------------------------------------------- 12 V

    [Fact]
    public void ChecksTheTwelveVoltSystemFromWakeUp()
    {
        var data = new BatteryData { Vin = SimulatorAdapter.SimulatedVin() };
        data.CarInfo["twelveVBattery"] = "Atlas BX B24 (flooded lead-acid)";
        data.CarInfo["birthday"] = DateTime.Today.AddYears(-2).ToString("yyyy-MM-dd");
        var check = new TwelveVoltCheck();

        data.TwelveVoltVolts = 12.62;
        data.DcDcState = 0;
        data.DcDcAmps = 0;
        check.Observe(data, Evening);
        data.TwelveVoltVolts = 13.9;
        data.DcDcVolts = 13.9;
        data.DcDcState = 1;
        for (var s = 1; s <= 20; s++)
        {
            data.DcDcAmps = 22 + s % 3;
            check.Observe(data, Evening.AddSeconds(s));
        }

        var report = check.Report(data);
        Assert.Equal(12.62, report.LowestBeforeSupport!.Value, 3);
        Assert.False(report.Lithium);
        Assert.Equal(CheckStatus.Pass, Item(report, "dcdc").Status);
        Assert.Equal(CheckStatus.Pass, Item(report, "lvBatteryWake").Status);
        Assert.Equal(CheckStatus.Pass, Item(report, "lvLoad").Status);
        Assert.Equal(CheckStatus.Pass, Item(report, "lvBattery").Status);
        Assert.Equal(CheckStatus.Pass, report.Overall);
    }

    [Fact]
    public void FlagsAWeakBatteryAFaultAndAnOldLeadAcidBattery()
    {
        var data = new BatteryData { TwelveVoltVolts = 11.8, DcDcState = 0 };
        data.CarInfo["birthday"] = DateTime.Today.AddYears(-6).ToString("yyyy-MM-dd");
        var check = new TwelveVoltCheck();
        check.Observe(data, Evening);
        data.DcDcState = 6;
        data.DcDcFaulted = true;
        check.Observe(data, Evening.AddSeconds(2));

        var report = check.Report(data);
        Assert.Equal(CheckStatus.Fail, Item(report, "lvBatteryWake").Status);
        Assert.Equal(CheckStatus.Fail, Item(report, "dcdc").Status);
        Assert.Equal(CheckStatus.Attention, Item(report, "lvBattery").Status);
    }

    [Fact]
    public void RecognisesALithiumLowVoltageBattery()
    {
        var data = new BatteryData { DcDcVolts = 15.6, DcDcState = 1, DcDcAmps = 25 };
        var check = new TwelveVoltCheck();
        check.Observe(data, Evening);

        var report = check.Report(data);
        Assert.True(report.Lithium);
        Assert.Equal(CheckStatus.Pass, Item(report, "dcdc").Status);
        Assert.Equal(CheckStatus.NotAvailable, Item(report, "lvBatteryWake").Status);
        Assert.Equal(CheckStatus.Pass, Item(report, "lvBattery").Status);
    }

    // ---------------------------------------------------------------- storage and trends

    [Fact]
    public void KeepsSnapshotsAndFindsTheEarlierOne()
    {
        using var db = new TempDatabase();
        var vin = SimulatorAdapter.SimulatedVin();
        db.Database.SaveSnapshot(vin, Snapshot(Evening, g => 3.86 + g * 0.0001));
        db.Database.SaveSnapshot(vin, Snapshot(Evening, g => 3.86)); // same time stamp: kept once
        db.Database.SaveSnapshot(vin, Snapshot(Evening.AddHours(9), g => 3.85));

        Assert.Equal(2, db.Database.ListSnapshots(vin).Count);
        var earlier = db.Database.SnapshotBefore(vin, Evening.AddHours(10))!;
        Assert.Equal(Evening, earlier.TakenAt);
        Assert.Equal(3.86 + 50 * 0.0001, earlier.Volts[50], 6);
        Assert.Null(db.Database.SnapshotBefore(vin, Evening.AddHours(2)));
    }

    [Fact]
    public void SavedChecksKeepTheirTestResults()
    {
        using var db = new TempDatabase();
        var vin = SimulatorAdapter.SimulatedVin();
        var tests = new TestSummary([new GroupFlag(56, 0.21, GroupFinding.Higher)], [], CheckStatus.Pass, "4 pass", CheckStatus.Attention, "1 attention, 3 pass");
        db.Database.AddCheck(new CheckRecord
        {
            Vin = vin, Started = Evening, Ended = Evening.AddMinutes(5), Source = "test",
            IsolationKOhm = 4500, PackResistanceMilliOhm = 61.6, Tests = tests,
        });

        var check = Assert.Single(db.Database.ListChecks(vin));
        Assert.Equal(4500, check.IsolationKOhm);
        Assert.Equal(61.6, check.PackResistanceMilliOhm);
        Assert.Equal(56, Assert.Single(check.Tests!.Resistance!).Index);
        Assert.Empty(check.Tests.Overnight!);
        Assert.Equal(CheckStatus.Attention, check.Tests.TwelveVolt);
    }

    [Fact]
    public void UpgradesADatabaseFromTheFirstVersion()
    {
        var path = Path.Combine(Path.GetTempPath(), $"otb-v1-{Guid.NewGuid():N}.db");
        try
        {
            using (var old = new SqliteConnection($"Data Source={path};Pooling=False"))
            {
                old.Open();
                using var create = old.CreateCommand();
                create.CommandText = """
                    CREATE TABLE cars (vin TEXT PRIMARY KEY, model TEXT, model_year INTEGER, first_seen TEXT NOT NULL, last_seen TEXT NOT NULL,
                        pack_key TEXT, original_kwh REAL, notes TEXT);
                    CREATE TABLE checks (id INTEGER PRIMARY KEY AUTOINCREMENT, vin TEXT, model TEXT, model_year INTEGER, started TEXT NOT NULL,
                        ended TEXT NOT NULL, source TEXT NOT NULL, platform TEXT, odometer_km REAL, soh REAL, original_kwh REAL, original_source TEXT,
                        current_kwh REAL, usable_kwh REAL, cell_spread_mv REAL, soc REAL, active_alerts INTEGER NOT NULL DEFAULT 0,
                        buyer_summary TEXT, summary_text TEXT, report_html TEXT);
                    CREATE TABLE alert_episodes (id INTEGER PRIMARY KEY AUTOINCREMENT, vin TEXT NOT NULL, code TEXT NOT NULL, start_at TEXT NOT NULL,
                        end_at TEXT, ended_with_session INTEGER NOT NULL DEFAULT 0);
                    CREATE TABLE recordings (path TEXT PRIMARY KEY, vin TEXT, started TEXT NOT NULL, ended TEXT, frames INTEGER, size_bytes INTEGER,
                        source TEXT, notes TEXT);
                    INSERT INTO checks (vin, started, ended, source, soh) VALUES ('5YJ3E1EB8JF100123', '2026-01-01T00:00:00+00:00', '2026-01-01T00:05:00+00:00', 'old', 90.1);
                    PRAGMA user_version = 1;
                    """;
                create.ExecuteNonQuery();
            }

            using var db = new AppDatabase(path);
            var check = Assert.Single(db.ListChecks());
            Assert.Equal(90.1, check.StateOfHealthPercent);
            Assert.Null(check.Tests);
            db.SaveSnapshot("5YJ3E1EB8JF100123", Snapshot(Evening, _ => 3.86));
            Assert.Single(db.ListSnapshots("5YJ3E1EB8JF100123"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void FollowsACarAcrossChecks()
    {
        CheckRecord Check(int day, double soh, double? resistance, params int[] flagged) => new()
        {
            Vin = "V", Started = Evening.AddDays(day), Ended = Evening.AddDays(day), Source = "test",
            StateOfHealthPercent = soh, CurrentKWh = 71.6 * soh / 88.9, CellSpreadMv = 17, IsolationKOhm = 4500, PackResistanceMilliOhm = resistance,
            Tests = resistance is null ? null : new TestSummary(flagged.Select(i => new GroupFlag(i, 0.2, GroupFinding.Higher)).ToList(), null, null, null, null, null),
        };

        List<CheckRecord> checks = [Check(200, 88.1, 63, 56), Check(0, 88.9, 61, 56, 12), Check(100, 88.5, null)];

        var trends = CarTrends.Build(checks);
        var health = trends.Single(t => t.Title == "State of health");
        Assert.Equal([88.9, 88.5, 88.1], health.Points.Select(p => p.Value));
        Assert.Equal("88.1%", health.Latest);
        Assert.Equal(2, trends.Single(t => t.Title == "Pack resistance").Points.Count);

        var groups = CarTrends.Groups(checks);
        Assert.Equal(56, groups[0].Index);
        Assert.Equal("Cell group 57: higher resistance in 2 of 2 charging tests", groups[0].Describe());
        Assert.Equal(12, groups[1].Index);
    }

    // ---------------------------------------------------------------- simulators end to end

    [Fact]
    public async Task TheNextMorningSimulatorShowsTheLeakingGroup()
    {
        var evening = (await Run(SimulatedCar.Model3, seconds: 60)).ChargeTestStatus().ParkedSnapshot!;
        var morning = (await Run(SimulatedCar.Model3NextMorning, seconds: 60)).ChargeTestStatus().ParkedSnapshot!;

        var result = CellDrift.Compare(evening, morning)!;
        Assert.Equal(10, result.Elapsed.TotalHours, 0.1);
        var flagged = Assert.Single(result.Flagged);
        Assert.Equal(SimulatorAdapter.OvernightValues.LeakingGroup, flagged.Index);
        Assert.Equal(-SimulatorAdapter.OvernightValues.LostMillivolts, flagged.ChangeMv, 1);
    }

    [Fact]
    public async Task TheHomeChargingSimulatorPassesTheChargerCheck()
    {
        var monitor = await Run(SimulatedCar.Model3HomeCharging, seconds: 150);
        var report = monitor.ChargerReport();

        Assert.True(report.Measuring);
        Assert.Equal("48 A single-phase charger", report.Charger);
        Assert.StartsWith("Single-phase, 240 V", report.Supply);
        Assert.InRange(report.EfficiencyPercent!.Value, 85, 93);
        Assert.All(report.Findings, f => Assert.Equal(CheckStatus.Pass, f.Status));

        // Charging resistance is measured too, at the much smaller home-charging current.
        Assert.Equal(SimulatorAdapter.ChargingValues.WeakGroup, Assert.Single(monitor.ChargeTestStatus().Result!.Flagged).Index);
    }

    [Fact]
    public async Task TheParkedSimulatorsWakeUpWithAHealthyTwelveVoltBattery()
    {
        var model3 = (await Run(SimulatedCar.Model3, seconds: 10)).TwelveVoltReport();
        Assert.Equal(12.62, model3.LowestBeforeSupport!.Value, 1);
        Assert.Equal(CheckStatus.Pass, Item(model3, "dcdc").Status);
        Assert.Equal(CheckStatus.Pass, Item(model3, "lvBatteryWake").Status);

        var modelS = (await Run(SimulatedCar.ModelS, seconds: 10)).TwelveVoltReport();
        Assert.Equal(12.5, modelS.LowestBeforeSupport!.Value, 1);
        Assert.Equal(CheckStatus.Pass, Item(modelS, "dcdc").Status);
    }

    // ---------------------------------------------------------------- helpers

    private static async Task<BatteryMonitor> Run(SimulatedCar car, double seconds)
    {
        var monitor = new BatteryMonitor(new AutoDetectProfile());
        DateTimeOffset? first = null;
        await foreach (var frame in new SimulatorAdapter(car, TimeSpan.Zero).StreamAsync(() => monitor.Profile.MonitoredIds, _ => { }, CancellationToken.None))
        {
            monitor.Process(frame);
            first ??= frame.Timestamp;
            if ((frame.Timestamp - first.Value).TotalSeconds > seconds)
                break;
        }

        return monitor;
    }

    private static ChargerReport Charger(BatteryData data, double? packAmps)
    {
        var check = new ChargerCheck();
        for (var s = 0; s <= 60; s++)
            check.Observe(data, Evening.AddSeconds(s), packAmps);
        return check.Report(data);
    }

    private static void Feed(ChargeTest test, double seconds, double amps, DateTimeOffset? from = null)
    {
        var time = from ?? Evening;
        for (var elapsed = 0.0; elapsed < seconds; elapsed += 0.25, time = time.AddMilliseconds(250))
        {
            var data = new BatteryData { PackCurrent = amps, PackVoltage = 370, SocUiPercent = 60 };
            for (var g = 0; g < 96; g++)
                data.BrickVoltages[g] = 3.861 + 0.0005 * amps / 10 + 0.001 * Math.Sin(g);
            test.Observe(data, time);
        }
    }

    private static CellSnapshot Snapshot(DateTimeOffset at, Func<int, double> volts) =>
        new(at, Enumerable.Range(0, 96).ToDictionary(g => g, volts), 60, 20, 22);

    private static CheckItem Item(ChargerReport report, string key) => report.Findings.Single(f => f.Key == key);

    private static CheckItem Item(TwelveVoltReport report, string key) => report.Findings.Single(f => f.Key == key);

    private static CanFrame Frame(uint id, Action<byte[]> fill, int length = 8)
    {
        var data = new byte[length];
        fill(data);
        return new CanFrame(id, data, Evening);
    }

    private sealed class TempDatabase : IDisposable
    {
        private readonly string _path = Path.Combine(Path.GetTempPath(), $"otb-{Guid.NewGuid():N}.db");

        public TempDatabase() => Database = new AppDatabase(_path);

        public AppDatabase Database { get; }

        public void Dispose()
        {
            Database.Dispose();
            File.Delete(_path);
        }
    }
}

using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Can;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Tests;

public class AlertTests
{
    private static readonly AlertCatalog Catalog = AlertCatalog.Model3Platform;
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Catalog_loads_with_battery_controllers_first_and_no_clash_with_battery_messages()
    {
        var batteryIds = new Model3Profile().MonitoredIds.Take(9).Select(i => i.Id);

        Assert.Equal(42, Catalog.Messages.Count);
        Assert.True(Catalog.Messages.Sum(m => m.Alerts.Count) > 2000);
        Assert.Contains(Catalog.Messages[0].Ecu, new[] { "BMS", "HVP" });
        Assert.Empty(Catalog.Messages.Select(m => m.Id).Intersect(batteryIds));
    }

    [Fact]
    public void Every_hand_written_description_matches_a_real_alert()
    {
        var unknown = AlertDescriptions.CuratedCodes.Where(code => Catalog.Find(code) is null).ToList();

        Assert.Empty(unknown);
        Assert.False(Catalog.Find("BMS_a064")!.Interpreted);
    }

    [Fact]
    public void Decodes_one_page_of_a_paged_matrix()
    {
        Assert.True(Catalog.TryGetMessage(0x320, out var bms));
        var frame = bms.Encode(1, ["BMS_a064"]);

        var states = bms.Decode(frame).ToDictionary(s => s.Code, s => s.Active);

        Assert.True(states["BMS_a064"]);
        Assert.False(states["BMS_a062"]);
        Assert.DoesNotContain("BMS_a017", states.Keys); // page 0 is not in this frame
        Assert.Equal(1, frame[0] & 0x0F);
        Assert.Equal(1 << 7, frame[0] & 0x80); // a064: page 1, bit 7
    }

    [Fact]
    public void Decodes_a_single_page_matrix()
    {
        Assert.True(Catalog.TryGetMessage(0x3AA, out var hvp));

        var states = hvp.Decode(hvp.Encode(0, ["HVP_w036"])).ToDictionary(s => s.Code, s => s.Active);

        Assert.True(states["HVP_w036"]);
        Assert.Equal(states.Count - 1, states.Values.Count(active => !active));
    }

    [Theory]
    [InlineData("SW_Brick_OV", "cell group over-voltage")]
    [InlineData("gndMonIntrptLineSide", "ground monitor interrupt line side")]
    [InlineData("DCDCNotOperational", "DC-DC converter not operational")]
    [InlineData("HW_BMB_OTP_Uncorrctbl", "hardware module monitoring board OTP uncorrctbl")]
    [InlineData("12VNotSupported", "12V not supported")]
    public void Interprets_internal_names(string internalName, string expected)
    {
        Assert.Equal(expected, AlertDescriptions.Interpret(internalName));
    }

    [Fact]
    public void Model3_profile_records_alert_states()
    {
        var profile = new Model3Profile();
        var data = new BatteryData();
        Catalog.TryGetMessage(0x320, out var bms);

        profile.Process(new CanFrame(bms!.Id, bms.Encode(1, ["BMS_a064"]), T0), data);

        Assert.True(data.CarAlerts["BMS_a064"]);
        Assert.False(data.CarAlerts["BMS_a062"]);
    }

    [Fact]
    public void Tracker_debounces_and_records_episodes()
    {
        var tracker = new AlertTracker();
        var data = new BatteryData { Platform = VehiclePlatform.Model3Family };
        var health = HealthCalculator.Evaluate(data);

        data.CarAlerts["BMS_a064"] = true;
        tracker.Update(data, health, T0);
        tracker.Update(data, health, T0.AddSeconds(1));
        Assert.Empty(tracker.Current);

        tracker.Update(data, health, T0.AddSeconds(3));
        var current = Assert.Single(tracker.Current);
        Assert.Equal("BMS_a064", current.Definition.Code);
        Assert.Equal(T0, current.Since);

        data.CarAlerts["BMS_a064"] = false;
        tracker.Update(data, health, T0.AddSeconds(10));
        tracker.Update(data, health, T0.AddSeconds(11));
        Assert.Single(tracker.Current);
        tracker.Update(data, health, T0.AddSeconds(13));
        Assert.Empty(tracker.Current);

        var history = Assert.Single(tracker.History());
        Assert.Equal(1, history.Occurrences);
        Assert.Equal(T0.AddSeconds(10), history.LastSeen);
        Assert.False(history.ActiveNow);
    }

    [Fact]
    public void Short_blips_are_ignored()
    {
        var tracker = new AlertTracker();
        var data = new BatteryData();
        var health = HealthCalculator.Evaluate(data);

        data.CarAlerts["BMS_a064"] = true;
        tracker.Update(data, health, T0);
        data.CarAlerts["BMS_a064"] = false;
        tracker.Update(data, health, T0.AddSeconds(1));
        tracker.Update(data, health, T0.AddSeconds(10));

        Assert.Empty(tracker.Current);
        Assert.Empty(tracker.History());
    }

    [Fact]
    public void History_persists_per_vin_across_sessions()
    {
        var folder = Path.Combine(Path.GetTempPath(), "tbh-alerts-" + Guid.NewGuid().ToString("N"));
        try
        {
            var store = new AlertHistoryStore(folder);
            var data = new BatteryData { Vin = "5YJ3E1EB8JF100123" };
            var health = HealthCalculator.Evaluate(data);
            data.CarAlerts["PCS_a019"] = true;

            var first = new AlertTracker(store);
            first.Update(data, health, T0);
            first.Update(data, health, T0.AddSeconds(5));
            first.EndSession(T0.AddMinutes(1));

            var second = new AlertTracker(store);
            second.Update(new BatteryData { Vin = data.Vin }, health, T0.AddDays(1));

            var entry = Assert.Single(second.History());
            Assert.Equal("PCS_a019", entry.Definition.Code);
            Assert.Equal(T0.AddMinutes(1), entry.LastSeen);
            Assert.Empty(second.Current);
        }
        finally
        {
            if (Directory.Exists(folder))
                Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void Tool_checks_flag_imbalance_only_at_rest()
    {
        var data = new BatteryData { PackCurrent = 0.5 };
        for (var i = 0; i < 96; i++)
            data.BrickVoltages[i] = i == 10 ? 3.830 : 3.860;

        var atRest = ToolDiagnostics.Evaluate(data, HealthCalculator.Evaluate(data)).Where(t => t.Active).Select(t => t.Code).ToList();
        data.PackCurrent = -60;
        var underLoad = ToolDiagnostics.Evaluate(data, HealthCalculator.Evaluate(data)).Where(t => t.Active).Select(t => t.Code);

        Assert.Equal([ToolDiagnostics.CellSpreadElevated], atRest);
        Assert.Empty(underLoad);
    }

    [Fact]
    public void Tool_checks_flag_capacity_and_cell_limits()
    {
        var data = new BatteryData { InitialFullPackKWh = 80, NominalFullPackKWh = 54, BrickVoltageMax = 4.3, BrickVoltageMin = 3.9 };

        var active = ToolDiagnostics.Evaluate(data, HealthCalculator.Evaluate(data)).Where(t => t.Active).Select(t => t.Code).ToList();

        Assert.Contains(ToolDiagnostics.BelowWarrantyCapacity, active);
        Assert.Contains(ToolDiagnostics.CellOverVoltage, active);
        Assert.Contains(ToolDiagnostics.CellSpreadHigh, active); // 400 mV between min and max
        Assert.DoesNotContain(ToolDiagnostics.CapacityWorn, active);
    }

    [Fact]
    public async Task Simulated_model3_alerts_show_as_current_and_history()
    {
        var profile = new AutoDetectProfile();
        var monitor = new BatteryMonitor(profile);
        var tracker = new AlertTracker();
        var frames = await AdapterTests.Take(new SimulatorAdapter(SimulatedCar.Model3, TimeSpan.Zero).StreamAsync(() => profile.MonitoredIds, _ => { }, CancellationToken.None), count: 4000);

        for (var i = 0; i < frames.Count; i++)
        {
            monitor.Process(frames[i]);
            if (i % 20 == 0)
            {
                var data = monitor.Snapshot();
                tracker.Update(data, HealthCalculator.Evaluate(data), T0.AddMilliseconds(i * 25));
            }
        }

        Assert.Contains(tracker.Current, a => a.Definition.Code == SimulatorAdapter.Model3Values.PersistentAlert);
        Assert.Contains(tracker.History(), h => h.Definition.Code == SimulatorAdapter.Model3Values.TransientAlert && !h.ActiveNow);
        Assert.True(tracker.Coverage.Supported);
        Assert.Equal(tracker.Coverage.MessagesKnown, tracker.Coverage.MessagesReceived);
    }

    [Fact]
    public async Task Obdlink_running_out_of_filter_memory_keeps_the_most_important_ids()
    {
        string[] bus = ["352 00 00 7C 0D 00 09 00 09", "132 C6 90 08 00 00 00 00 00", "320 01 00 00 00 00 00 00 00"];
        var link = new FakeElmLink(stn: true, bus) { MaxFilters = 2 };
        var adapter = new ElmAdapter(() => link) { BusCheckDuration = TimeSpan.FromMilliseconds(200) };
        var log = new List<string>();
        MonitoredId[] ids = [new(0x352), new(0x132), new(0x320)];

        var received = await AdapterTests.Take(adapter.StreamAsync(() => ids, log.Add, CancellationToken.None), count: bus.Length + 2);

        Assert.Equal([0x352u, 0x132u], received.Skip(bus.Length).Select(f => f.Id));
        Assert.Contains(log, l => l.Contains("filter memory is full after 2 of 3", StringComparison.Ordinal));
        Assert.Equal("STM", link.Commands[^1]);
    }
}

using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Buyer;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Tests;

public class BuyerCheckTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private static readonly AlertCoverage Covered = new(true, 42, 42);
    private static readonly AlertCoverage NotCovered = new(false, 0, 0);

    [Fact]
    public async Task Simulated_model3_passes_except_for_the_expired_warranty()
    {
        var items = await RunSimulator(SimulatedCar.Model3);

        Assert.Equal(CheckStatus.Pass, Item(items, "health").Status);
        Assert.Equal(CheckStatus.Pass, Item(items, "redFlags").Status);
        Assert.Equal(CheckStatus.Pass, Item(items, "packHistory").Status);
        Assert.Equal(CheckStatus.Pass, Item(items, "isolation").Status);
        Assert.Equal(CheckStatus.Pass, Item(items, "charging").Status);
        Assert.Equal(CheckStatus.Pass, Item(items, "twelveVolt").Status);
        Assert.Equal(CheckStatus.Pass, Item(items, "cellBalance").Status);
        Assert.Equal(CheckStatus.Pass, Item(items, "infotainment").Status);
        var warranty = Item(items, "warranty");
        Assert.Equal(CheckStatus.Attention, warranty.Status); // built June 2018: 8 years ran out in June 2026
        Assert.Contains("ended", warranty.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Simulated_model_s_shows_its_replaced_pack()
    {
        var items = await RunSimulator(SimulatedCar.ModelS);

        var pack = Item(items, "packHistory");
        Assert.Equal(CheckStatus.Attention, pack.Status);
        Assert.Contains("probably replaced", pack.Summary, StringComparison.Ordinal);
        Assert.Equal(CheckStatus.NotAvailable, Item(items, "redFlags").Status);
        Assert.Equal(CheckStatus.NotAvailable, Item(items, "isolation").Status);
        Assert.Equal(CheckStatus.NotAvailable, Item(items, "charging").Status);
        Assert.Equal(CheckStatus.Attention, Item(items, "cellBalance").Status);
    }

    [Fact]
    public void A_crash_seen_before_fails_the_red_flag_check()
    {
        var crash = new AlertHistoryEntry(AlertDefinitions.Find("RCM_a000"), 1, Now.AddDays(-3), Now.AddDays(-3), ActiveNow: false);

        var item = Evaluate(new BatteryData(), history: [crash], coverage: Covered).Single(i => i.Key == "redFlags");

        Assert.Equal(CheckStatus.Fail, item.Status);
        Assert.Contains("RCM_a000, seen before", item.Summary, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(5_000, CheckStatus.Pass)]
    [InlineData(600, CheckStatus.Attention)]
    [InlineData(150, CheckStatus.Fail)]
    public void Rates_isolation_resistance(double kOhm, CheckStatus expected)
    {
        var data = new BatteryData { IsolationResistanceKOhm = kOhm, PackVoltage = 380 };

        Assert.Equal(expected, Evaluate(data).Single(i => i.Key == "isolation").Status);
    }

    [Fact]
    public void Matching_odometers_and_plausible_energy_pass()
    {
        var data = new BatteryData { OdometerKm = 150_000, BatteryOdometerKm = 149_500, DischargeTotalKWh = 30_000 };

        var item = Evaluate(data).Single(i => i.Key == "packHistory");

        Assert.Equal(CheckStatus.Pass, item.Status);
        Assert.Contains("original pack", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Too_little_lifetime_energy_for_the_mileage_suggests_a_replaced_pack()
    {
        var data = new BatteryData { OdometerKm = 200_000, DriveDischargeTotalKWh = 9_000 };

        var item = Evaluate(data).Single(i => i.Key == "packHistory");

        Assert.Equal(CheckStatus.Attention, item.Status);
        Assert.Contains("replaced", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Mostly_fast_charged_cars_get_attention()
    {
        var data = new BatteryData { AcChargeTotalKWh = 2_000, DcChargeTotalKWh = 18_000 };

        var item = Evaluate(data).Single(i => i.Key == "charging");

        Assert.Equal(CheckStatus.Attention, item.Status);
        Assert.StartsWith("90%", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Standard_range_model3_over_100k_miles_is_out_of_battery_warranty()
    {
        var data = new BatteryData { Vin = "5YJ3E1EA0MF000000", InitialFullPackKWh = 55, OdometerKm = 170_000 };

        var item = Evaluate(data).Single(i => i.Key == "warranty");

        Assert.Equal(CheckStatus.Attention, item.Status);
        Assert.Contains("100,000-mile", item.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public void Summary_counts_the_verdicts()
    {
        CheckItem[] items =
        [
            new("a", "A", CheckStatus.Pass, ""),
            new("b", "B", CheckStatus.Attention, ""),
            new("c", "C", CheckStatus.Fail, ""),
            new("d", "D", CheckStatus.Pass, ""),
            new("e", "E", CheckStatus.NotAvailable, ""),
        ];

        Assert.Equal("1 fail, 1 attention, 2 pass", BuyerCheck.Summarize(items));
    }

    private static IReadOnlyList<CheckItem> Evaluate(BatteryData data, IReadOnlyList<AlertHistoryEntry>? history = null, AlertCoverage? coverage = null) =>
        BuyerCheck.Evaluate(new CheckInput(data, HealthCalculator.Evaluate(data), [], history ?? [], coverage ?? NotCovered, Now));

    private static CheckItem Item(IReadOnlyList<CheckItem> items, string key) => items.Single(i => i.Key == key);

    private static async Task<IReadOnlyList<CheckItem>> RunSimulator(SimulatedCar car)
    {
        var profile = new AutoDetectProfile();
        var monitor = new BatteryMonitor(profile);
        var tracker = new AlertTracker();
        var frames = await AdapterTests.Take(new SimulatorAdapter(car, TimeSpan.Zero).StreamAsync(() => profile.MonitoredIds, _ => { }, CancellationToken.None), count: 2500);
        for (var i = 0; i < frames.Count; i++)
        {
            monitor.Process(frames[i]);
            if (i % 20 == 0)
            {
                var snapshot = monitor.Snapshot();
                tracker.Update(snapshot, HealthCalculator.Evaluate(snapshot), Now.AddMilliseconds(i * 25));
            }
        }

        var data = monitor.Snapshot();
        var health = HealthCalculator.Evaluate(data);
        tracker.Update(data, health, Now.AddMinutes(5));
        return BuyerCheck.Evaluate(new CheckInput(data, health, tracker.Current, tracker.History(), tracker.Coverage, Now));
    }
}

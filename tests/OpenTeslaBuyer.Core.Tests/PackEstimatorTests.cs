using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Tests;

public class PackEstimatorTests
{
    [Fact]
    public void Early_vin_battery_code_names_the_pack()
    {
        var estimate = Estimate("5YJSA1H14EF000000", nominal: 74.6, km: 150_000);

        Assert.Equal("85", estimate!.Pack.Key);
        Assert.True(estimate.Confident);
        Assert.Equal(74.6 / 81.5, estimate.Health, 6);
    }

    [Fact]
    public void Eighty_four_cell_groups_mean_the_14_module_60_kWh_pack()
    {
        var estimate = Estimate("5YJSA1E10EF000000", nominal: 54.0, km: 120_000, cellGroups: 84);

        Assert.Equal("60-14", estimate!.Pack.Key);
        Assert.True(estimate.Confident);
    }

    [Fact]
    public void Expected_wear_decides_between_packs_that_both_fit()
    {
        // A 2017 Model S at 160,000 km reading 76 kWh is more likely a 90 kWh pack with typical wear
        // than a 75 kWh pack with none.
        var estimate = Estimate("5YJSA1E20HF000000", nominal: 76.0, km: 160_000, cellGroups: 96);

        Assert.Equal("90", estimate!.Pack.Key);
        Assert.Contains(estimate.Alternatives, p => p.Key == "75");
    }

    [Fact]
    public void Large_reading_on_a_late_car_means_the_100_kWh_pack()
    {
        var estimate = Estimate("5YJSA1E20KF000000", nominal: 92.0, km: 60_000, cellGroups: 96);

        Assert.Equal("100", estimate!.Pack.Key);
        Assert.True(estimate.Confident);
    }

    [Fact]
    public void Replacement_pack_newer_than_the_car_is_still_found()
    {
        // A 2013 car reading more than any 2013 pack holds must have a later replacement pack.
        var estimate = Estimate("5YJSA1E10DF000000", nominal: 85.0, km: 250_000, cellGroups: 96);

        Assert.Contains(estimate!.Pack.Key, new[] { "90", "100" });
    }

    [Fact]
    public void No_estimate_without_vin_or_for_other_models()
    {
        Assert.Null(PackEstimator.Estimate(new BatteryData { NominalFullPackKWh = 75 }));
        Assert.Null(Estimate(SimulatorAdapter.SimulatedVin(SimulatedCar.Model3), nominal: 70, km: 100_000));
    }

    [Fact]
    public void Expected_loss_follows_teslas_200k_mile_figure()
    {
        Assert.Equal(0, PackEstimator.ExpectedLoss(0), 6);
        Assert.Equal(0.05, PackEstimator.ExpectedLoss(50_000), 6);
        Assert.Equal(0.12, PackEstimator.ExpectedLoss(321_869), 3);
    }

    [Fact]
    public void Health_uses_the_estimate_unless_a_pack_is_chosen_or_typed()
    {
        var data = new BatteryData
        {
            Platform = VehiclePlatform.LegacyModelSX,
            Vin = "5YJSA1H14EF000000",
            NominalFullPackKWh = 74.6,
            OdometerKm = 150_000,
        };

        var estimated = HealthCalculator.Evaluate(data);
        var chosen = HealthCalculator.Evaluate(data, packKey: "90");
        var typed = HealthCalculator.Evaluate(data, manualOriginalKWh: 80.0, packKey: "90");

        Assert.Equal(CapacitySource.Estimated, estimated.OriginalSource);
        Assert.Equal(81.5, estimated.OriginalKWh);
        Assert.Contains(estimated.Notes, n => n.StartsWith("Original capacity is estimated", StringComparison.Ordinal));
        Assert.Equal(CapacitySource.PackChosen, chosen.OriginalSource);
        Assert.Equal(85.8, chosen.OriginalKWh);
        Assert.Equal(CapacitySource.Manual, typed.OriginalSource);
        Assert.Equal(80.0, typed.OriginalKWh);
    }

    [Fact]
    public async Task Simulated_model_s_gets_an_estimate_without_typing_anything()
    {
        var profile = new AutoDetectProfile();
        var monitor = new BatteryMonitor(profile);

        foreach (var frame in await AdapterTests.Take(new SimulatorAdapter(SimulatedCar.ModelS, TimeSpan.Zero).StreamAsync(() => profile.MonitoredIds, _ => { }, CancellationToken.None), count: 1500))
            monitor.Process(frame);

        var data = monitor.Snapshot();
        var health = HealthCalculator.Evaluate(data);
        Assert.Equal(96, data.CellGroupCount);
        Assert.Equal(CapacitySource.Estimated, health.OriginalSource);
        Assert.Equal("85", health.Pack!.Key);
        Assert.NotNull(health.StateOfHealthPercent);
    }

    private static PackEstimate? Estimate(string vin, double nominal, double km, int? cellGroups = null) =>
        PackEstimator.Estimate(new BatteryData { Vin = vin, NominalFullPackKWh = nominal, OdometerKm = km, CellGroupCount = cellGroups });
}

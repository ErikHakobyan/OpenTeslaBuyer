using OpenTeslaBuyer.Core.Battery;

namespace OpenTeslaBuyer.Core.Tests;

public class HealthCalculatorTests
{
    [Fact]
    public void Computes_health_from_bms_values()
    {
        var data = new BatteryData
        {
            InitialFullPackKWh = 80.0,
            NominalFullPackKWh = 72.0,
            NominalRemainingKWh = 40.0,
            EnergyBufferKWh = 3.0,
            RatedWhPerKm = 150.0,
            DischargeTotalKWh = 40_000,
        };

        var report = HealthCalculator.Evaluate(data);

        Assert.Equal(CapacitySource.Bms, report.OriginalSource);
        Assert.Equal(90.0, report.StateOfHealthPercent!.Value, 6);
        Assert.Equal(10.0, report.DegradationPercent!.Value, 6);
        Assert.Equal(8.0, report.LostKWh!.Value, 6);
        Assert.Equal(69.0, report.UsableKWh!.Value, 6);
        Assert.Equal(37.0, report.UsableRemainingKWh!.Value, 6);
        Assert.Equal(460.0, report.FullRangeKm!.Value, 6);
        Assert.Equal(500.0, report.EquivalentFullCycles!.Value, 6);
        Assert.Equal(HealthRating.Excellent, report.Rating);
    }

    [Fact]
    public void Manual_original_capacity_wins_over_bms()
    {
        var data = new BatteryData { InitialFullPackKWh = 80.0, NominalFullPackKWh = 60.0 };

        var report = HealthCalculator.Evaluate(data, manualOriginalKWh: 75.0);

        Assert.Equal(CapacitySource.Manual, report.OriginalSource);
        Assert.Equal(80.0, report.StateOfHealthPercent!.Value, 6);
        Assert.Equal(HealthRating.Good, report.Rating);
    }

    [Fact]
    public void Degradation_never_goes_negative()
    {
        var report = HealthCalculator.Evaluate(new BatteryData { InitialFullPackKWh = 80.0, NominalFullPackKWh = 80.6 });

        Assert.True(report.StateOfHealthPercent > 100);
        Assert.Equal(0, report.DegradationPercent);
        Assert.Equal(0, report.LostKWh);
    }

    [Fact]
    public void Rates_capacity_below_warranty_threshold_as_poor()
    {
        var report = HealthCalculator.Evaluate(new BatteryData { InitialFullPackKWh = 80.0, NominalFullPackKWh = 54.0 });

        Assert.Equal(HealthRating.Poor, report.Rating);
    }

    [Fact]
    public void Without_data_everything_is_unknown()
    {
        var report = HealthCalculator.Evaluate(new BatteryData());

        Assert.Null(report.StateOfHealthPercent);
        Assert.Null(report.FullRangeKm);
        Assert.Equal(HealthRating.Unknown, report.Rating);
        Assert.Empty(report.Notes);
    }

    [Fact]
    public void Cell_spread_prefers_individual_bricks_and_ignores_it_under_load()
    {
        var data = new BatteryData { BrickVoltageMax = 3.9, BrickVoltageMin = 3.8, PackCurrent = -40 };
        for (var i = 0; i < 96; i++)
            data.BrickVoltages[i] = 3.860 + (i == 10 ? -0.030 : 0);

        var report = HealthCalculator.Evaluate(data);

        Assert.Equal(30.0, report.CellSpreadMv!.Value, 6);
        Assert.Contains(report.Notes, n => n.Contains("under load", StringComparison.Ordinal));
    }
}

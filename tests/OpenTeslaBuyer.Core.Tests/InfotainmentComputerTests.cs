using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Buyer;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Tests;

public class InfotainmentComputerTests
{
    [Theory]
    [InlineData("2018-06-15", InfotainmentComputer.Mcu2, true)]
    [InlineData("2022-09-01", InfotainmentComputer.Mcu3, true)]
    [InlineData("2021-12-01", null, false)]
    public void Model3_uses_the_build_date(string built, string? expected, bool certain)
    {
        var data = Model3("5YJ3E1EB8JF100123", built);

        var estimate = InfotainmentComputer.Estimate(data)!;

        Assert.Equal(certain, estimate.Certain);
        if (expected is not null)
            Assert.Equal(expected, estimate.Display);
        Assert.False(estimate.Mcu1AsBuilt);
    }

    [Theory]
    [InlineData("5YJ3E1EB0MF000000", InfotainmentComputer.Mcu2, true)] // 2021
    [InlineData("5YJ3E1EB0NF000000", null, false)]                      // 2022: either
    [InlineData("5YJ3E1EB0PF000000", InfotainmentComputer.Mcu3, true)] // 2023
    public void Model3_without_build_date_falls_back_to_model_year(string vin, string? expected, bool certain)
    {
        var estimate = InfotainmentComputer.Estimate(Model3(vin, built: null))!;

        Assert.Equal(certain, estimate.Certain);
        if (expected is not null)
            Assert.Equal(expected, estimate.Display);
    }

    [Fact]
    public void Older_model_s_was_built_with_mcu1()
    {
        var estimate = InfotainmentComputer.Estimate(ModelS("5YJSA1E26FF100456"))!;

        Assert.True(estimate.Mcu1AsBuilt);
        Assert.StartsWith(InfotainmentComputer.Mcu1, estimate.Display, StringComparison.Ordinal);
        Assert.Contains("upgraded", estimate.Display, StringComparison.Ordinal);
    }

    [Fact]
    public void Model_year_2018_model_s_could_be_either()
    {
        var estimate = InfotainmentComputer.Estimate(ModelS("5YJSA1E26JF100456"))!;

        Assert.False(estimate.Certain);
        Assert.True(estimate.Mcu1AsBuilt);
    }

    [Fact]
    public void Later_and_refreshed_model_s_have_newer_computers()
    {
        Assert.Equal(InfotainmentComputer.Mcu2, InfotainmentComputer.Estimate(ModelS("5YJSA1E26KF100456"))!.Display);

        var refreshed = new BatteryData { Vin = "5YJSA1E26NF100456", Platform = VehiclePlatform.Model3Family };
        Assert.Equal(InfotainmentComputer.Mcu3, InfotainmentComputer.Estimate(refreshed)!.Display);
    }

    [Fact]
    public void Estimate_appears_with_the_car_details_and_in_the_buyer_check()
    {
        var data = ModelS("5YJSA1E26FF100456");

        var details = CarInfo.WithEstimates(data);
        var item = BuyerCheck.Evaluate(new CheckInput(data, HealthCalculator.Evaluate(data), [], [], new(false, 0, 0), DateTimeOffset.UtcNow))
            .Single(i => i.Key == "infotainment");

        Assert.Contains("MCU1", details["processor"], StringComparison.Ordinal);
        Assert.Equal(CheckStatus.Attention, item.Status);
    }

    private static BatteryData Model3(string vin, string? built)
    {
        var data = new BatteryData { Vin = vin, Platform = VehiclePlatform.Model3Family };
        if (built is not null)
            data.CarInfo["birthday"] = built;
        return data;
    }

    private static BatteryData ModelS(string vin) => new() { Vin = vin, Platform = VehiclePlatform.LegacyModelSX };
}

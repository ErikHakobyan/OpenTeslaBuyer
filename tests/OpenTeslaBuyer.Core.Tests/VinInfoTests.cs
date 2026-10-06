using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Tests;

public class VinInfoTests
{
    [Fact]
    public void Decodes_model_year_plant_and_maker()
    {
        var vin = new VinInfo("5YJ3E1EB5JF100123");

        Assert.Equal(TeslaModel.Model3, vin.Model);
        Assert.Equal(2018, vin.ModelYear);
        Assert.Equal("Fremont, California", vin.Plant);
        Assert.Equal("Tesla, USA", vin.Maker);
    }

    [Theory]
    [InlineData("1M8GDM9AXKP042788", true)] // standard textbook example with an X check digit
    [InlineData("1M8GDM9A1KP042788", false)]
    public void Validates_north_american_check_digit(string vin, bool valid)
    {
        Assert.Equal(valid, new VinInfo(vin).CheckDigitValid);
    }

    [Fact]
    public void Check_digit_does_not_apply_to_european_vins()
    {
        Assert.Null(new VinInfo("XP7YGCEK0RB123456").CheckDigitValid);
    }

    [Fact]
    public void Simulated_vin_is_valid()
    {
        Assert.True(new VinInfo(Adapters.SimulatorAdapter.SimulatedVin()).CheckDigitValid);
    }
}

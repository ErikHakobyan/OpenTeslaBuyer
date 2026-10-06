using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Can;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Tests;

public class CarInfoTests
{
    [Fact]
    public void Decodes_all_gateway_configuration_pages()
    {
        var info = new Dictionary<string, string>();

        for (var page = 1; page <= 4; page++)
            GatewayConfig.Decode(GatewayConfig.Encode(page, SimulatorAdapter.Model3Values.Configuration, "US"), info);

        Assert.Equal("United States (US)", info["country"]);
        Assert.Equal("United States", info["mapRegion"]);
        Assert.Equal("HW2.5", info["autopilotHardware"]);
        Assert.Equal("Enhanced Autopilot", info["autopilot"]);
        Assert.Equal("Model 3", info["chassis"]);
        Assert.Equal("AWD (dual motor)", info["drivetrain"]);
        Assert.Equal("74 kWh", info["pack"]);
        Assert.Equal("Premium", info["connectivity"]);
        Assert.Equal("2018-06-15", info["birthday"]);
    }

    [Fact]
    public void Ignores_an_implausible_birthday()
    {
        var info = new Dictionary<string, string>();

        GatewayConfig.Decode(GatewayConfig.Encode(4, new Dictionary<string, ulong> { ["birthday"] = 12 }), info);

        Assert.DoesNotContain("birthday", info.Keys);
    }

    [Theory]
    [InlineData('U', 'S', "United States (US)")]
    [InlineData('S', 'U', "United States (US)")]
    [InlineData('D', 'E', "Germany (DE)")]
    [InlineData('1', 'X', null)]
    public void Reads_two_letter_countries(char first, char second, string? expected)
    {
        Assert.Equal(expected, CarInfo.Country((byte)first, (byte)second));
    }

    [Fact]
    public void Model_s_reports_its_country()
    {
        var data = new BatteryData();

        new ModelSXProfile().Process(new CanFrame(ModelSXSignals.Country, "GB"u8.ToArray(), DateTimeOffset.UtcNow), data);

        Assert.Equal("United Kingdom (GB)", data.CarInfo["country"]);
    }

    [Fact]
    public async Task Simulated_model3_reports_its_configuration()
    {
        var profile = new AutoDetectProfile();
        var monitor = new BatteryMonitor(profile);

        foreach (var frame in await AdapterTests.Take(new SimulatorAdapter(SimulatedCar.Model3, TimeSpan.Zero).StreamAsync(() => profile.MonitoredIds, _ => { }, CancellationToken.None), count: 1500))
            monitor.Process(frame);

        var text = CarInfo.ToText(monitor.Snapshot().CarInfo);
        Assert.Contains("Map region: United States", text, StringComparison.Ordinal);
        Assert.Contains("Autopilot hardware: HW2.5", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Text_summary_has_the_key_facts()
    {
        var data = new BatteryData { Vin = SimulatorAdapter.SimulatedVin(), InitialFullPackKWh = 80.5, NominalFullPackKWh = 71.6, OdometerKm = 112_345 };
        var health = HealthCalculator.Evaluate(data);
        var alert = new ActiveAlert(AlertDefinitions.Find("BMS_a064"), DateTimeOffset.UtcNow);

        var text = ReportWriter.ToText(data, health, miles: false, [alert]);

        Assert.StartsWith("Tesla Model 3 2018 battery health", text, StringComparison.Ordinal);
        Assert.Contains($"VIN: {data.Vin}", text, StringComparison.Ordinal);
        Assert.Contains("Degradation: ", text, StringComparison.Ordinal);
        Assert.Contains("Active alerts: BMS_a064 State-of-charge imbalance", text, StringComparison.Ordinal);
        Assert.DoesNotContain("Rated range", text, StringComparison.Ordinal); // unknown values are left out
    }
}

using System.Text;
using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Can;
using OpenTeslaBuyer.Core.Vehicles;
using static OpenTeslaBuyer.Core.Vehicles.ModelSXSignals;

namespace OpenTeslaBuyer.Core.Tests;

public class ModelSXTests
{
    [Fact]
    public void Decodes_ten_bit_energy_message()
    {
        var profile = new ModelSXProfile();
        var data = new BatteryData();

        for (var i = 0; i < 4; i++)
            profile.Process(Bits10Frame(full: 77.3, remaining: 40.0, buffer: 3.4), data);

        Assert.Equal(EnergyLayout.Bits10, data.EnergyLayout);
        Assert.Equal(77.3, data.NominalFullPackKWh!.Value, 2);
        Assert.Equal(40.0, data.NominalRemainingKWh!.Value, 2);
        Assert.Equal(3.4, data.EnergyBufferKWh!.Value, 2);
        Assert.Equal(VehiclePlatform.LegacyModelSX, data.Platform);
    }

    [Fact]
    public void Eleven_bit_energy_message_handles_packs_above_102_kwh()
    {
        var profile = new ModelSXProfile();
        var data = new BatteryData();

        for (var i = 0; i < 4; i++)
            profile.Process(Bits11Frame(full: 102.4, remaining: 60.0, buffer: 4.0, toCharge: 30.0), data);

        Assert.Equal(EnergyLayout.Bits11, data.EnergyLayout);
        Assert.Equal(102.4, data.NominalFullPackKWh!.Value, 2);
    }

    [Fact]
    public void State_of_charge_settles_layouts_that_both_look_plausible()
    {
        // Read as 10-bit, this 11-bit frame still looks sane (85.0 kWh full, 60.0 remaining, 0.6 buffer),
        // but it implies about 70% charge while the car reports about 33%.
        var frame = Bits11Frame(full: 85.0, remaining: 30.0, buffer: 3.2, toCharge: 40.0).Data;
        var detector = new EnergyLayoutDetector(EnergyLayout.Bits11, EnergyLayout.Bits10);

        for (var i = 0; i < 4; i++)
            Assert.Equal(EnergyLayout.Auto, detector.Detect(frame, socUiPercent: null));

        Assert.Equal(EnergyLayout.Bits11, detector.Detect(frame, socUiPercent: 32.8));
    }

    [Fact]
    public void Decodes_brick_voltages_and_module_temperatures()
    {
        var profile = new ModelSXProfile();
        var data = new BatteryData();
        var voltages = new byte[8];
        BrickPage.EncodeRaw(voltages, 2);
        BrickVoltageSlots[0].Encode(voltages, 3.9810);
        BrickVoltageSlots[1].Encode(voltages, 3.9790);
        BrickVoltageSlots[2].EncodeRaw(voltages, 0x3FFF); // not available
        BrickVoltageSlots[3].Encode(voltages, 3.9800);
        var temperatures = new byte[8];
        BrickPage.EncodeRaw(temperatures, VoltagePages + 1);
        TemperatureSlots[0].Encode(temperatures, 24.4);
        TemperatureSlots[1].Encode(temperatures, -5.0);
        TemperatureSlots[2].Encode(temperatures, 25.0);
        TemperatureSlots[3].Encode(temperatures, 23.9);

        profile.Process(new CanFrame(BrickData, voltages, DateTimeOffset.UtcNow), data);
        profile.Process(new CanFrame(BrickData, temperatures, DateTimeOffset.UtcNow), data);

        Assert.Equal([8, 9, 11], data.BrickVoltages.Keys);
        Assert.Equal(3.9790, data.BrickVoltages[9], 3);
        Assert.Equal(-5.0, data.TempMinC!.Value, 1);
        Assert.Equal(25.0, data.TempMaxC!.Value, 1);
    }

    [Fact]
    public void Reads_vin_soc_odometer_and_lifetime_energy()
    {
        var profile = new ModelSXProfile();
        var data = new BatteryData();
        const string vin = "5YJSA1E26FF100456";

        profile.Process(VinFrame(0, vin[..7]), data);
        profile.Process(VinFrame(1, vin[7..14]), data);
        profile.Process(VinFrame(2, vin[14..]), data);
        profile.Process(Frame(SocStatus, d => SocUi.Encode(d, 71.5), length: 3), data);
        profile.Process(Frame(Odometer, d => OdometerMiles.Encode(d, 1000.0), length: 4), data);
        profile.Process(Frame(KwhCounter, d =>
        {
            DischargeTotal.Encode(d, 61_234.5);
            ChargeTotal.Encode(d, 66_890.2);
        }), data);

        Assert.Equal(vin, data.Vin);
        Assert.Equal(71.5, data.SocUiPercent!.Value, 1);
        Assert.Equal(1609.344, data.OdometerKm!.Value, 3);
        Assert.Equal(61_234.5, data.DischargeTotalKWh!.Value, 3);
        Assert.Equal(66_890.2, data.ChargeTotalKWh!.Value, 3);
    }

    [Fact]
    public void Missing_original_capacity_asks_for_it()
    {
        var data = new BatteryData { Platform = VehiclePlatform.LegacyModelSX, NominalFullPackKWh = 74.6 };

        var withoutOriginal = HealthCalculator.Evaluate(data);
        var withOriginal = HealthCalculator.Evaluate(data, manualOriginalKWh: 81.5);

        Assert.Null(withoutOriginal.StateOfHealthPercent);
        Assert.Contains(withoutOriginal.Notes, n => n.Contains("before 2021", StringComparison.Ordinal));
        Assert.Equal(74.6 / 81.5 * 100, withOriginal.StateOfHealthPercent!.Value, 6);
    }

    [Theory]
    [InlineData(SimulatedCar.Model3, VehiclePlatform.Model3Family)]
    [InlineData(SimulatedCar.ModelS, VehiclePlatform.LegacyModelSX)]
    public async Task Auto_detection_picks_the_platform_and_keeps_early_frames(SimulatedCar car, VehiclePlatform expected)
    {
        var profile = new AutoDetectProfile();
        var monitor = new BatteryMonitor(profile);
        var allIds = profile.MonitoredIds.Count;

        foreach (var frame in await AdapterTests.Take(new SimulatorAdapter(car, TimeSpan.Zero).StreamAsync(() => profile.MonitoredIds, _ => { }, CancellationToken.None), count: 1500))
            monitor.Process(frame);

        var data = monitor.Snapshot();
        Assert.Equal(expected, data.Platform);
        Assert.Equal(SimulatorAdapter.SimulatedVin(car), data.Vin);
        Assert.NotNull(data.NominalFullPackKWh);
        Assert.Equal(96, data.BrickVoltages.Count);
        Assert.True(profile.MonitoredIds.Count < allIds);
    }

    [Fact]
    public void Simulated_model_s_vin_is_valid()
    {
        var vin = new VinInfo(SimulatorAdapter.SimulatedVin(SimulatedCar.ModelS));

        Assert.True(vin.CheckDigitValid);
        Assert.Equal(TeslaModel.ModelS, vin.Model);
        Assert.Equal(2015, vin.ModelYear);
    }

    private static CanFrame Bits10Frame(double full, double remaining, double buffer) => Frame(Energy, d =>
    {
        EnergyStatus.Bits10.NominalFullPack.Encode(d, full);
        EnergyStatus.Bits10.NominalRemaining.Encode(d, remaining);
        EnergyStatus.Bits10.ExpectedRemaining.Encode(d, remaining - 1);
        EnergyStatus.Bits10.IdealRemaining.Encode(d, remaining);
        EnergyStatus.Bits10.EnergyBuffer.Encode(d, buffer);
    });

    private static CanFrame Bits11Frame(double full, double remaining, double buffer, double toCharge) => Frame(Energy, d =>
    {
        EnergyStatus.Bits11.NominalFullPack.Encode(d, full);
        EnergyStatus.Bits11.NominalRemaining.Encode(d, remaining);
        EnergyStatus.Bits11.ExpectedRemaining.Encode(d, remaining - 1);
        EnergyStatus.Bits11.IdealRemaining.Encode(d, remaining + 0.5);
        EnergyStatus.Bits11.EnergyToChargeComplete.Encode(d, toCharge);
        EnergyStatus.Bits11.EnergyBuffer.Encode(d, buffer);
    });

    private static CanFrame VinFrame(byte page, string text) => Frame(Vin, d =>
    {
        d[0] = page;
        Encoding.ASCII.GetBytes(text).CopyTo(d, 1);
    });

    private static CanFrame Frame(uint id, Action<byte[]> fill, int length = 8)
    {
        var data = new byte[length];
        fill(data);
        return new CanFrame(id, data, DateTimeOffset.UtcNow);
    }
}

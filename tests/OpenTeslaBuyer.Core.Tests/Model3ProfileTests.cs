using System.Text;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Can;
using OpenTeslaBuyer.Core.Vehicles;
using static OpenTeslaBuyer.Core.Vehicles.Model3Signals;

namespace OpenTeslaBuyer.Core.Tests;

public class Model3ProfileTests
{
    [Fact]
    public void Detects_and_decodes_multiplexed_energy_layout()
    {
        var profile = new Model3Profile();
        var data = new BatteryData();

        for (var i = 0; i < 6; i++)
            profile.Process(i % 2 == 0 ? MultiplexedPage0(71.6, 46.0) : MultiplexedPage1(3.0), data);

        Assert.Equal(EnergyLayout.Multiplexed, data.EnergyLayout);
        Assert.Equal(71.6, data.NominalFullPackKWh!.Value, 2);
        Assert.Equal(46.0, data.NominalRemainingKWh!.Value, 2);
        Assert.Equal(3.0, data.EnergyBufferKWh!.Value, 2);
    }

    [Fact]
    public void Detects_and_decodes_legacy_energy_layout()
    {
        var profile = new Model3Profile();
        var data = new BatteryData();

        for (var i = 0; i < 5; i++)
            profile.Process(LegacyFrame(full: 75.2, remaining: 50.0 - i * 0.1, buffer: 3.1), data);

        Assert.Equal(EnergyLayout.Bits11, data.EnergyLayout);
        Assert.Equal(75.2, data.NominalFullPackKWh!.Value, 2);
        Assert.Equal(49.6, data.NominalRemainingKWh!.Value, 2);
        Assert.Equal(3.1, data.EnergyBufferKWh!.Value, 2);
    }

    [Fact]
    public void Decodes_nothing_from_energy_frames_until_layout_is_known()
    {
        var profile = new Model3Profile();
        var data = new BatteryData();

        profile.Process(MultiplexedPage0(71.6, 46.0), data);

        Assert.Equal(EnergyLayout.Auto, data.EnergyLayout);
        Assert.Null(data.NominalFullPackKWh);
    }

    [Fact]
    public void Forced_layout_decodes_immediately()
    {
        var profile = new Model3Profile(EnergyLayout.Bits11);
        var data = new BatteryData();

        profile.Process(LegacyFrame(full: 60.1, remaining: 30.0, buffer: 2.5), data);

        Assert.Equal(60.1, data.NominalFullPackKWh!.Value, 2);
    }

    [Fact]
    public void Assembles_vin_from_three_pages()
    {
        var profile = new Model3Profile();
        var data = new BatteryData();
        const string vin = "5YJ3E1EB5JF100123";

        profile.Process(VinFrame(0x11, vin[3..10]), data);
        Assert.Null(data.Vin);
        profile.Process(VinFrame(0x10, vin[..3]), data);
        profile.Process(VinFrame(0x12, vin[10..]), data);

        Assert.Equal(vin, data.Vin);
    }

    [Fact]
    public void Collects_brick_voltages_by_page()
    {
        var profile = new Model3Profile();
        var data = new BatteryData();
        var frame = new byte[8];
        BrickPage.EncodeRaw(frame, 2);
        BrickSlots[0].Encode(frame, 3.8612);
        BrickSlots[1].Encode(frame, 3.8605);
        BrickSlots[2].Encode(frame, 3.8620);

        profile.Process(new CanFrame(BrickVoltages, frame, DateTimeOffset.UtcNow), data);

        Assert.Equal([6, 7, 8], data.BrickVoltages.Keys);
        Assert.Equal(3.8605, data.BrickVoltages[7], 4);
    }

    [Fact]
    public void Reads_original_capacity_and_soc()
    {
        var profile = new Model3Profile();
        var data = new BatteryData();
        var frame = new byte[8];
        SocUi.Encode(frame, 64.3);
        InitialFullPackEnergy.Encode(frame, 80.5);

        profile.Process(new CanFrame(SocStatus, frame, DateTimeOffset.UtcNow), data);

        Assert.Equal(64.3, data.SocUiPercent!.Value, 2);
        Assert.Equal(80.5, data.InitialFullPackKWh!.Value, 2);
    }

    private static CanFrame MultiplexedPage0(double full, double remaining)
    {
        var d = new byte[8];
        EnergyStatus.Multiplexed.Page.EncodeRaw(d, 0);
        EnergyStatus.Multiplexed.NominalFullPack.Encode(d, full);
        EnergyStatus.Multiplexed.NominalRemaining.Encode(d, remaining);
        EnergyStatus.Multiplexed.IdealRemaining.Encode(d, remaining);
        return new CanFrame(Energy, d, DateTimeOffset.UtcNow);
    }

    private static CanFrame MultiplexedPage1(double buffer)
    {
        var d = new byte[8];
        EnergyStatus.Multiplexed.Page.EncodeRaw(d, 1);
        EnergyStatus.Multiplexed.EnergyBuffer.Encode(d, buffer);
        return new CanFrame(Energy, d, DateTimeOffset.UtcNow);
    }

    private static CanFrame LegacyFrame(double full, double remaining, double buffer)
    {
        var d = new byte[8];
        EnergyStatus.Bits11.NominalFullPack.Encode(d, full);
        EnergyStatus.Bits11.NominalRemaining.Encode(d, remaining);
        EnergyStatus.Bits11.ExpectedRemaining.Encode(d, remaining - 1);
        EnergyStatus.Bits11.IdealRemaining.Encode(d, remaining);
        EnergyStatus.Bits11.EnergyBuffer.Encode(d, buffer);
        return new CanFrame(Energy, d, DateTimeOffset.UtcNow);
    }

    private static CanFrame VinFrame(byte page, string text)
    {
        var d = new byte[8];
        d[0] = page;
        Encoding.ASCII.GetBytes(text).CopyTo(d, 1);
        return new CanFrame(Vin, d, DateTimeOffset.UtcNow);
    }
}

using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Vehicles;

/// <summary>
/// Powertrain-CAN ("CAN3") battery signals of the Model S 2012–2021 and Model X 2015–2021, from community reverse
/// engineering: wk057's "Tesla Model S CAN Deciphering" notes (as transcribed in thezim/DBCTools) and the Open Vehicle
/// Monitoring System's Model S module (MIT). Untested against a car by this project.
/// </summary>
public static class ModelSXSignals
{
    /// <summary>0x102 BMS current and voltage. Only the voltage is decoded: the published current scaling is inconsistent.</summary>
    public const uint HvBusStatus = 0x102;

    public static readonly Signal PackVoltage = new(0, 16, 0.01);

    /// <summary>0x302 state of charge (0.1% resolution, as decoded by OVMS).</summary>
    public const uint SocStatus = 0x302;

    public static readonly Signal SocMin = new(0, 10, 0.1);
    public static readonly Signal SocUi = new(10, 10, 0.1);

    /// <summary>0x382 BMS energy status; see <see cref="EnergyStatus"/> for its layouts.</summary>
    public const uint Energy = 0x382;

    /// <summary>0x3D2 lifetime energy, same layout as on the Model 3 (1 Wh resolution).</summary>
    public const uint KwhCounter = 0x3D2;

    public static readonly Signal DischargeTotal = new(0, 32, 0.001);
    public static readonly Signal ChargeTotal = new(32, 32, 0.001);

    /// <summary>0x508 VIN: pages 0, 1 and 2 carry characters 1–7, 8–14 and 15–17 in bytes 1–7.</summary>
    public const uint Vin = 0x508;

    public static readonly byte[] VinPages = [0, 1, 2];

    /// <summary>0x398 country the car is configured for: two ASCII letters in bytes 0–1 (as read by OVMS).</summary>
    public const uint Country = 0x398;

    /// <summary>0x562 battery odometer, in miles (about every 10 s); lower than the car's after a pack replacement.</summary>
    public const uint BatteryOdometer = 0x562;

    public static readonly Signal BatteryOdometerMiles = new(0, 32, 0.001);

    /// <summary>0x5D8 vehicle odometer, in miles.</summary>
    public const uint Odometer = 0x5D8;

    public static readonly Signal OdometerMiles = new(0, 32, 0.001);

    /// <summary>
    /// 0x6F2 BMS brick data. Pages 0–23 carry four brick voltages each (brick = page × 4 + slot);
    /// later pages carry four module temperatures each (sensor = (page − 24) × 4 + slot). 14-bit fields.
    /// </summary>
    public const uint BrickData = 0x6F2;

    public const int VoltagePages = 24;

    public static readonly Signal BrickPage = new(0, 8);
    public static readonly Signal[] BrickVoltageSlots = [new(8, 14, 0.000305), new(22, 14, 0.000305), new(36, 14, 0.000305), new(50, 14, 0.000305)];
    public static readonly Signal[] TemperatureSlots = [new(8, 14, 0.0122, Signed: true), new(22, 14, 0.0122, Signed: true), new(36, 14, 0.0122, Signed: true), new(50, 14, 0.0122, Signed: true)];
}

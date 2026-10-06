using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Vehicles;

/// <summary>
/// Vehicle-CAN battery signals of the Model 3 / Model Y (2017–2023 builds), from community reverse engineering:
/// joshwardell/model3dbc (MIT) and onyx-m2/onyx-m2-dbc. Layouts can change with firmware; record a log if values look wrong.
/// </summary>
public static class Model3Signals
{
    /// <summary>0x352 BMS_energyStatus; see <see cref="Vehicles.EnergyStatus"/> for its layouts.</summary>
    public const uint Energy = 0x352;

    /// <summary>0x292 BMS_socStatus, including the pack's beginning-of-life ("when new") energy.</summary>
    public const uint SocStatus = 0x292;

    public static readonly Signal SocMin = new(0, 10, 0.1);
    public static readonly Signal SocUi = new(10, 10, 0.1);
    public static readonly Signal SocMax = new(20, 10, 0.1);
    public static readonly Signal SocAverage = new(30, 10, 0.1);
    public static readonly Signal InitialFullPackEnergy = new(40, 10, 0.1);

    /// <summary>0x132 BMS_hvBusStatus.</summary>
    public const uint HvBusStatus = 0x132;

    public static readonly Signal PackVoltage = new(0, 16, 0.01);
    public static readonly Signal PackCurrent = new(16, 15, -0.1, Signed: true);

    /// <summary>0x332 BMS_bmbMinMax: page 0 temperatures, page 1 brick voltages.</summary>
    public const uint BrickMinMax = 0x332;

    public static readonly Signal BrickMinMaxPage = new(0, 2);
    public static readonly Signal TempMax = new(16, 8, 0.5, -40);
    public static readonly Signal TempMin = new(24, 8, 0.5, -40);
    public static readonly Signal BrickVoltageMax = new(2, 12, 0.002);
    public static readonly Signal BrickVoltageMin = new(16, 12, 0.002);
    public static readonly Signal BrickNumberMax = new(32, 7, 1, 1);
    public static readonly Signal BrickNumberMin = new(40, 7, 1, 1);

    /// <summary>0x3D2 BMS_kwhCounter: lifetime energy in and out of the pack.</summary>
    public const uint KwhCounter = 0x3D2;

    public static readonly Signal DischargeTotal = new(0, 32, 0.001);
    public static readonly Signal ChargeTotal = new(32, 32, 0.001);

    /// <summary>0x33A UI_rangeSOC.</summary>
    public const uint RangeSoc = 0x33A;

    public static readonly Signal RatedWhPerMile = new(32, 10);

    /// <summary>0x3B6 DI_odometerStatus.</summary>
    public const uint Odometer = 0x3B6;

    public static readonly Signal OdometerKm = new(0, 32, 0.001);

    /// <summary>0x401 BMS_brickVoltages: page n carries bricks 3n, 3n+1 and 3n+2.</summary>
    public const uint BrickVoltages = 0x401;

    public static readonly Signal BrickPage = new(0, 8);
    public static readonly Signal[] BrickSlots = [new(16, 16, 0.0001), new(32, 16, 0.0001), new(48, 16, 0.0001)];

    /// <summary>0x212 BMS_status: includes the high-voltage isolation resistance (10 kΩ per bit).</summary>
    public const uint BmsStatus = 0x212;

    public static readonly Signal IsolationResistance = new(19, 10, 10);

    /// <summary>0x3F2 BMS_kwhCountersMultiplexed: page 0 AC charged, 1 DC charged, 2 regen, 3 drive discharge (lifetime kWh).</summary>
    public const uint KwhCountersMultiplexed = 0x3F2;

    public static readonly Signal KwhCounterPage = new(0, 4);
    public static readonly Signal KwhCounterValue = new(8, 32, 0.001);

    /// <summary>0x288 EPBleftStatus: the left parking-brake controller's filtered 12 V supply reading.</summary>
    public const uint ParkingBrakeLeft = 0x288;

    public static readonly Signal TwelveVolt = new(37, 12, 0.00544368);

    /// <summary>
    /// 0x264 PCS_chgLineStatus: the AC supply while charging from an AC charger. Voltage scale is 0.0333 V in model3dbc and
    /// 0.033 V in onyx-m2-dbc, both about 1/30 V.
    /// </summary>
    public const uint ChargeLine = 0x264;

    public static readonly Signal AcVoltage = new(0, 14, 1.0 / 30);
    public static readonly Signal AcCurrent = new(14, 9, 0.1);
    public static readonly Signal AcInputPower = new(24, 8, 0.1);
    public static readonly Signal AcCurrentLimit = new(32, 10, 0.1);

    /// <summary>0x204 PCS_chgStatus: the onboard charger's state, the supply type, and which of its phase modules are on.</summary>
    public const uint ChargerStatus = 0x204;

    /// <summary>0 init, 1 idle, 2 startup, 3 wait for line voltage, 4 qualify line, 5 configure, 6 enabled, 7 shutdown, 8 faulted, 9 clear faults.</summary>
    public static readonly Signal ChargerMainState = new(0, 4);

    /// <summary>0 standby, 1 blocked, 2 enabled, 3 faulted.</summary>
    public static readonly Signal ChargerHvStatus = new(4, 2);

    /// <summary>0 unknown, 1 single phase, 2 three phase, 3 three phase delta.</summary>
    public static readonly Signal GridConfig = new(6, 2);

    public static readonly Signal[] PhaseEnabled = [new(8, 1), new(9, 1), new(10, 1)];
    public static readonly Signal ChargerMaxAcPower = new(24, 8, 0.1);

    /// <summary>0 48 A single-phase, 1 32 A single-phase, 2 three-phase, 3 unknown.</summary>
    public static readonly Signal ChargerVariant = new(59, 2);

    /// <summary>0x2B4 PCS_dcdcRailStatus: the DC-DC converter's low-voltage output.</summary>
    public const uint DcDcRail = 0x2B4;

    public static readonly Signal DcDcLowVoltage = new(0, 10, 0.0390625);
    public static readonly Signal DcDcOutputCurrent = new(24, 12, 0.1);

    /// <summary>0x224 PCS_dcdcStatus.</summary>
    public const uint DcDcStatus = 0x224;

    /// <summary>0 standby, 1 supplying 12 V, 2–4 precharging or discharging the high-voltage bus, 5 shutdown, 6 faulted.</summary>
    public static readonly Signal DcDcMainState = new(6, 4);

    public static readonly Signal DcDcFaulted = new(15, 1);
    public static readonly Signal DcDcOutputLimited = new(28, 1);

    /// <summary>0x405 VIN_info: pages 0x10, 0x11, 0x12 each carry up to 7 ASCII characters in bytes 1–7.</summary>
    public const uint Vin = 0x405;

    public static readonly byte[] VinPages = [0x10, 0x11, 0x12];
}

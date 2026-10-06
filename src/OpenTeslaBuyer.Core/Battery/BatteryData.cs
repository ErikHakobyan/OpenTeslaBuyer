namespace OpenTeslaBuyer.Core.Battery;

/// <summary>Everything decoded from the bus so far. Null means "not received (yet)".</summary>
public sealed class BatteryData
{
    public string? Vin { get; set; }

    /// <summary>Which family of battery messages the car uses; set by the vehicle profile.</summary>
    public VehiclePlatform Platform { get; set; }

    public EnergyLayout EnergyLayout { get; set; }

    /// <summary>The BMS's current estimate of the pack's full energy, buffer included.</summary>
    public double? NominalFullPackKWh { get; set; }

    public double? NominalRemainingKWh { get; set; }

    public double? IdealRemainingKWh { get; set; }

    public double? ExpectedRemainingKWh { get; set; }

    /// <summary>Energy held back below 0% displayed charge.</summary>
    public double? EnergyBufferKWh { get; set; }

    public double? EnergyToChargeCompleteKWh { get; set; }

    public bool? FullyCharged { get; set; }

    /// <summary>The pack's energy when new, as stored in the BMS ("beginning of life").</summary>
    public double? InitialFullPackKWh { get; set; }

    public double? SocUiPercent { get; set; }

    public double? SocMinPercent { get; set; }

    public double? SocMaxPercent { get; set; }

    public double? SocAveragePercent { get; set; }

    public double? PackVoltage { get; set; }

    public double? PackCurrent { get; set; }

    public double? BrickVoltageMax { get; set; }

    public double? BrickVoltageMin { get; set; }

    public int? BrickNumberMax { get; set; }

    public int? BrickNumberMin { get; set; }

    public double? TempMaxC { get; set; }

    public double? TempMinC { get; set; }

    public double? ChargeTotalKWh { get; set; }

    public double? DischargeTotalKWh { get; set; }

    public double? OdometerKm { get; set; }

    /// <summary>Distance recorded by the battery itself (pre-2021 Model S/X); differs from the car's after a pack swap.</summary>
    public double? BatteryOdometerKm { get; set; }

    /// <summary>Insulation resistance between the high-voltage system and the car body, measured by the BMS.</summary>
    public double? IsolationResistanceKOhm { get; set; }

    /// <summary>Lifetime energy charged from AC (home, destination) chargers.</summary>
    public double? AcChargeTotalKWh { get; set; }

    /// <summary>Lifetime energy charged from DC fast chargers (Superchargers and others).</summary>
    public double? DcChargeTotalKWh { get; set; }

    public double? RegenChargeTotalKWh { get; set; }

    /// <summary>Lifetime energy used for driving only (excludes parked consumption and charging losses).</summary>
    public double? DriveDischargeTotalKWh { get; set; }

    /// <summary>The 12 V supply as seen by a controller (with the car awake this is mostly the DC-DC output).</summary>
    public double? TwelveVoltVolts { get; set; }

    public double? RatedWhPerKm { get; set; }

    /// <summary>AC supply voltage while charging from an AC charger (Model 3 platform).</summary>
    public double? AcVolts { get; set; }

    public double? AcAmps { get; set; }

    public double? AcInputKw { get; set; }

    /// <summary>The most AC current charging may draw, as the charger sees it.</summary>
    public double? AcCurrentLimitAmps { get; set; }

    /// <summary>Raw <c>PCS_chgMainState</c>: 6 = charging, 8 = faulted.</summary>
    public int? ChargerState { get; set; }

    /// <summary>Raw <c>PCS_hvChargeStatus</c>: 2 = enabled, 3 = faulted.</summary>
    public int? ChargerHvStatus { get; set; }

    /// <summary>Raw <c>PCS_gridConfig</c>: 1 = single phase, 2 or 3 = three phase.</summary>
    public int? GridConfig { get; set; }

    /// <summary>Which of the charger's three phase modules are on (bit 0 = A, 1 = B, 2 = C).</summary>
    public int? ChargerPhases { get; set; }

    /// <summary>Raw <c>PCS_hwVariantType</c>: 0 = 48 A single-phase, 1 = 32 A single-phase, 2 = three-phase.</summary>
    public int? ChargerVariant { get; set; }

    public double? ChargerMaxAcKw { get; set; }

    /// <summary>The DC-DC converter's low-voltage output, which supplies the 12 V system while the car is awake.</summary>
    public double? DcDcVolts { get; set; }

    public double? DcDcAmps { get; set; }

    /// <summary>Raw <c>PCS_dcdcMainState</c>: 1 = supplying 12 V, 6 = faulted.</summary>
    public int? DcDcState { get; set; }

    public bool? DcDcFaulted { get; set; }

    public bool? DcDcLimited { get; set; }

    /// <summary>Cell groups in series, once the profile has seen every page of the cell voltage message.</summary>
    public int? CellGroupCount { get; set; }

    /// <summary>Per-brick (parallel cell group) voltages, keyed by zero-based brick index.</summary>
    public SortedDictionary<int, double> BrickVoltages { get; private set; } = [];

    public long FrameCount { get; set; }

    public DateTimeOffset? LastFrameAt { get; set; }

    public HashSet<uint> SeenIds { get; private set; } = [];

    /// <summary>Latest state of every car alert received, keyed by code (e.g. <c>BMS_a064</c>); true = active.</summary>
    public Dictionary<string, bool> CarAlerts { get; private set; } = [];

    /// <summary>Configuration and infotainment details, keyed as in <c>CarInfo.Fields</c>; values are display text.</summary>
    public Dictionary<string, string> CarInfo { get; private set; } = [];

    public BatteryData Clone()
    {
        var copy = (BatteryData)MemberwiseClone();
        copy.BrickVoltages = new SortedDictionary<int, double>(BrickVoltages);
        copy.SeenIds = [.. SeenIds];
        copy.CarAlerts = new Dictionary<string, bool>(CarAlerts);
        copy.CarInfo = new Dictionary<string, string>(CarInfo);
        return copy;
    }
}

/// <summary>Which layout of the BMS energy message the car's firmware uses (see <c>EnergyStatus</c>).</summary>
public enum EnergyLayout
{
    /// <summary>Not determined yet (or, as a user setting, "detect automatically").</summary>
    Auto,
    Multiplexed,
    Bits11,
    Bits10,
}

/// <summary>The two families of Tesla battery messages.</summary>
public enum VehiclePlatform
{
    /// <summary>Not determined yet (or, as a user setting, "detect automatically").</summary>
    Auto,

    /// <summary>Model 3, Model Y and the 2021+ Model S/X.</summary>
    Model3Family,

    /// <summary>Model S 2012–2021 and Model X 2015–2021.</summary>
    LegacyModelSX,
}

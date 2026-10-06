using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Vehicles;
using static OpenTeslaBuyer.Core.Alerts.AlertSeverity;

namespace OpenTeslaBuyer.Core.Alerts;

/// <summary>
/// Warnings this tool raises itself from the decoded battery data. They work on every supported model, including
/// pre-2021 Model S/X, whose own alert messages are not publicly decoded. Thresholds are deliberately conservative
/// rules of thumb, not Tesla's limits.
/// </summary>
public static class ToolDiagnostics
{
    public const string System = "Battery check (this tool)";

    private const double AtRestAmps = 5;

    public const string CellSpreadHigh = "TOOL_w001";
    public const string CellSpreadElevated = "TOOL_w002";
    public const string CellOverVoltage = "TOOL_w003";
    public const string CellUnderVoltage = "TOOL_w004";
    public const string PackHot = "TOOL_w005";
    public const string PackCold = "TOOL_w006";
    public const string ModuleTemperatureSpread = "TOOL_w007";
    public const string BelowWarrantyCapacity = "TOOL_w008";
    public const string CapacityWorn = "TOOL_w009";
    public const string LowCharge = "TOOL_w010";
    public const string VinCheckDigit = "TOOL_w011";
    public const string EnergyLayoutUnknown = "TOOL_w012";

    public static IReadOnlyList<AlertDefinition> Definitions { get; } =
    [
        Define(CellSpreadHigh, Critical, "Large cell imbalance", "With the car at rest, the highest and lowest cell groups differ by more than 50 mV. That points to a weak cell group or a balancing problem; have the pack checked."),
        Define(CellSpreadElevated, Warning, "Cell imbalance elevated", "With the car at rest, the highest and lowest cell groups differ by 20–50 mV. Balanced packs usually stay well below that. Re-check after a full charge and a rest."),
        Define(CellOverVoltage, Critical, "Cell group voltage above 4.25 V", "A cell group reads above 4.25 V, beyond the normal range of every Tesla cell chemistry."),
        Define(CellUnderVoltage, Critical, "Cell group voltage below 2.5 V", "A cell group reads below 2.5 V, below the normal range of every Tesla cell chemistry. It may be over-discharged."),
        Define(PackHot, Warning, "Battery hot", "A battery temperature reading is above 55 °C. Power and charging speed are reduced at these temperatures."),
        Define(PackCold, Info, "Battery very cold", "A battery temperature reading is below −20 °C. Expect reduced power, regenerative braking and charging speed until it warms up."),
        Define(ModuleTemperatureSpread, Warning, "Uneven battery temperatures", "Battery temperature readings differ by more than 10 °C. That can point to a cooling problem or a faulty sensor."),
        Define(BelowWarrantyCapacity, Critical, "Capacity below warranty threshold", $"Capacity is below the {HealthCalculator.WarrantyRetentionPercent:0}% Tesla's battery warranty guarantees. Check whether the car is still within the warranty's years and mileage."),
        Define(CapacityWorn, Warning, "Capacity noticeably reduced", "State of health is between 70% and 80%: noticeably more wear than usual for most packs."),
        Define(LowCharge, Info, "Low state of charge", "The battery is below 10%. Leaving a pack nearly empty for long periods accelerates wear."),
        Define(VinCheckDigit, Info, "VIN check digit mismatch", "The VIN read from the car fails its check digit, so it may have been read incorrectly."),
        Define(EnergyLayoutUnknown, Warning, "Capacity message not recognised", "The car's energy message does not match any known layout, so capacity cannot be shown. Force a layout under Advanced or send a recording."),
    ];

    public static AlertDefinition? Find(string code) => Definitions.FirstOrDefault(d => d.Code == code);

    /// <summary>The state of every tool check for the current data. Checks without enough data report inactive.</summary>
    public static IEnumerable<(string Code, bool Active)> Evaluate(BatteryData data, HealthReport health)
    {
        var atRest = data.PackCurrent is not { } amps || Math.Abs(amps) <= AtRestAmps;
        var spread = health.CellSpreadMv;
        yield return (CellSpreadHigh, atRest && spread > 50);
        yield return (CellSpreadElevated, atRest && spread is > 20 and <= 50);

        var bricks = data.BrickVoltages.Values;
        var highest = bricks.Count > 0 ? bricks.Max() : data.BrickVoltageMax;
        var lowest = bricks.Count > 0 ? bricks.Min() : data.BrickVoltageMin;
        yield return (CellOverVoltage, highest > 4.25);
        yield return (CellUnderVoltage, lowest < 2.5);

        yield return (PackHot, data.TempMaxC > 55);
        yield return (PackCold, data.TempMinC < -20);
        yield return (ModuleTemperatureSpread, data.TempMaxC - data.TempMinC > 10);

        var soh = health.StateOfHealthPercent;
        yield return (BelowWarrantyCapacity, soh < HealthCalculator.WarrantyRetentionPercent);
        yield return (CapacityWorn, soh is >= HealthCalculator.WarrantyRetentionPercent and < 80);

        yield return (LowCharge, data.SocUiPercent < 10);
        yield return (VinCheckDigit, data.Vin is { } vin && new VinInfo(vin).CheckDigitValid == false);

        var energyId = VehicleProfiles.EnergyMessageId(data.Platform);
        yield return (EnergyLayoutUnknown, data.Platform != VehiclePlatform.Auto
                                           && data.EnergyLayout == EnergyLayout.Auto
                                           && data.SeenIds.Contains(energyId)
                                           && data.SocUiPercent is not null
                                           && data.FrameCount > 5000);
    }

    private static AlertDefinition Define(string code, AlertSeverity severity, string title, string description) =>
        new(code, System, title, description, severity, AlertSource.Tool);
}

/// <summary>Looks up any alert code this tool knows, from the car or its own.</summary>
public static class AlertDefinitions
{
    public static AlertDefinition Find(string code) =>
        ToolDiagnostics.Find(code)
        ?? AlertCatalog.Model3Platform.Find(code)
        ?? new AlertDefinition(code, "Unknown", code, "This alert code is not in the catalog.", AlertSeverity.Warning, AlertSource.Car, Interpreted: true);
}

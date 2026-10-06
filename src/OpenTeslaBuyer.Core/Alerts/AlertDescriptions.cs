using System.Text.RegularExpressions;
using static OpenTeslaBuyer.Core.Alerts.AlertSeverity;

namespace OpenTeslaBuyer.Core.Alerts;

/// <summary>
/// Plain-language meaning of Tesla alert codes. Battery, high-voltage, charging and 12 V alerts are described by
/// hand; the rest are interpreted from Tesla's internal names (e.g. <c>SW_Brick_OV</c> → "cell group over-voltage")
/// and flagged as such. Tesla does not publish a full list; the owner's manual explains the alerts drivers see.
/// </summary>
public static partial class AlertDescriptions
{
    private static readonly Dictionary<string, string> Systems = new()
    {
        ["BMS"] = "Battery management system",
        ["HVP"] = "High-voltage controller (contactors, pyro fuse, current sensor)",
        ["PCS"] = "Power conversion system (on-board charger and DC-DC converter)",
        ["CP"] = "Charge port",
        ["CC"] = "Wall Connector",
        ["UMC"] = "Mobile Connector",
        ["FC"] = "DC fast charging",
        ["VCFRONT"] = "Front body controller (12 V power, thermal system)",
        ["VCLEFT"] = "Left body controller",
        ["VCRIGHT"] = "Right body controller",
        ["VCSEC"] = "Security controller (keys, locks)",
        ["DI"] = "Rear drive inverter",
        ["DIS"] = "Front drive inverter",
        ["PM"] = "Powertrain manager",
        ["PMS"] = "Powertrain manager (secondary)",
        ["GTW"] = "Gateway",
        ["UI"] = "Touchscreen computer",
        ["EPAS3P"] = "Power steering",
        ["EPBL"] = "Left parking brake",
        ["EPBR"] = "Right parking brake",
        ["RCM"] = "Restraints (airbags, seat belts)",
        ["SCCM"] = "Steering column controls",
        ["SCM"] = "Supplemental controller",
        ["SCS"] = "Supplemental controller",
        ["TAS"] = "Air suspension",
        ["DAS"] = "Autopilot computer",
        ["APP"] = "Autopilot computer",
        ["APS"] = "Autopilot computer",
    };

    /// <summary>Lower is more important to a battery check; decides polling and filter order.</summary>
    public static int Priority(string ecu) => ecu switch
    {
        "BMS" or "HVP" => 0,
        "PCS" or "CP" or "CC" or "UMC" or "FC" => 1,
        "VCFRONT" or "DI" or "DIS" or "GTW" => 2,
        _ => 3,
    };

    public static string SystemName(string ecu) => Systems.GetValueOrDefault(ecu, ecu);

    public static AlertDefinition Describe(string code, string ecu, string internalName)
    {
        var system = SystemName(ecu);
        if (Curated.TryGetValue(code, out var known))
            return new AlertDefinition(code, system, known.Title, known.Description, known.Severity, AlertSource.Car, internalName);

        var phrase = Interpret(internalName);
        var title = phrase.Length == 0 ? internalName : char.ToUpperInvariant(phrase[0]) + phrase[1..];
        var description = $"{system}: {phrase}.";
        return new AlertDefinition(code, system, title, description, GuessSeverity(code, ecu, internalName), AlertSource.Car, internalName, Interpreted: true);
    }

    /// <summary>Turns an internal name such as <c>SW_Brick_OV</c> or <c>gndMonIntrptLineSide</c> into words.</summary>
    public static string Interpret(string internalName)
    {
        var words = Tokens().Matches(internalName)
            .Select(m => Glossary.TryGetValue(m.Value, out var word) ? word : AsWord(m.Value))
            .Where(w => w.Length > 0);
        return string.Join(' ', words);
    }

    // Acronyms (OTP, CAN) and values (12V) keep their case; ordinary words are lower-cased.
    private static string AsWord(string token) =>
        token.Length > 1 && (token.All(char.IsUpper) || token.Any(char.IsDigit)) ? token : token.ToLowerInvariant();

    private static AlertSeverity GuessSeverity(string code, string ecu, string internalName)
    {
        var tokens = Tokens().Matches(internalName).Select(m => m.Value.ToLowerInvariant()).ToHashSet();
        if (tokens.Overlaps(["reset", "watchdog", "log", "upload", "request", "connected", "engineering", "build", "xcp", "uds", "retry", "active", "limited"]))
            return Info;
        if (Priority(ecu) == 0 && tokens.Overlaps(["ov", "uv", "ot", "weld", "welded", "isolation", "hvil", "crash", "pyro", "overdischarged"]))
            return Critical;
        return Warning;
    }

    internal static IEnumerable<string> CuratedCodes => Curated.Keys;

    // Numbers with V (12V), runs of capitals before a capitalised word (DCDCNot -> DCDC), words, digits.
    [GeneratedRegex("[0-9]+V|[0-9]+|[A-Z]{2,}(?=[A-Z][a-z])|[A-Z]?[a-z]+|[A-Z]+")]
    private static partial Regex Tokens();

    private static readonly Dictionary<string, string> Glossary = new(StringComparer.OrdinalIgnoreCase)
    {
        ["SW"] = "",
        ["HW"] = "hardware",
        ["OV"] = "over-voltage",
        ["UV"] = "under-voltage",
        ["OT"] = "over-temperature",
        ["OC"] = "over-current",
        ["MIA"] = "not responding",
        ["SNA"] = "not available",
        ["Brick"] = "cell group",
        ["BMB"] = "module monitoring board",
        ["Hvp"] = "high-voltage controller",
        ["Hvil"] = "high-voltage interlock",
        ["Ctr"] = "contactor",
        ["Cont"] = "contactor",
        ["Chg"] = "charger",
        ["Dch"] = "discharge",
        ["Dchg"] = "discharge",
        ["Pchg"] = "precharge",
        ["Fc"] = "fast-charge",
        ["Pcs"] = "charger/DC-DC",
        ["Cp"] = "charge port",
        ["Gtw"] = "gateway",
        ["Di"] = "drive inverter",
        ["Dcdc"] = "DC-DC converter",
        ["Lv"] = "12 V",
        ["Hv"] = "high-voltage",
        ["Irrational"] = "implausible",
        ["Rationality"] = "plausibility check",
        ["Soc"] = "state of charge",
        ["Nvram"] = "memory",
        ["Nvm"] = "memory",
        ["Nvmm"] = "memory",
        ["Eeprom"] = "memory",
        ["Tsense"] = "temperature sensor",
        ["Temp"] = "temperature",
        ["Tmp"] = "temperature",
        ["Therm"] = "thermal",
        ["Thml"] = "thermal",
        ["Gnd"] = "ground",
        ["Mon"] = "monitor",
        ["Intrpt"] = "interrupt",
        ["Vref"] = "reference voltage",
        ["Robin"] = "redundant monitor",
        ["Ext"] = "external",
        ["Pwr"] = "power",
        ["Comms"] = "communication",
        ["Vbat"] = "12 V battery voltage",
        ["Ptc"] = "heater",
        ["Cmp"] = "compressor",
        ["Exv"] = "expansion valve",
        ["Refrig"] = "refrigerant",
        ["Pres"] = "pressure",
        ["Disch"] = "discharge",
        ["Suct"] = "suction",
        ["Sns"] = "sensor",
        ["EFuse"] = "electronic fuse",
        ["IBS"] = "12 V battery sensor",
        ["GFCI"] = "ground-fault protection",
        ["CCID"] = "ground-fault protection",
        ["Prox"] = "plug detection",
        ["Evse"] = "charging station",
        ["Ens"] = "",
        ["Rq"] = "request",
        ["Req"] = "request",
        ["Msmtch"] = "mismatch",
        ["Blckd"] = "blocked",
        ["Pt"] = "powertrain",
        ["Bat"] = "battery",
        ["Fbk"] = "feedback",
        ["Sc"] = "Supercharger",
        ["Ac"] = "AC",
        ["Dc"] = "DC",
    };

    private sealed record Known(AlertSeverity Severity, string Title, string Description);

    private static readonly Dictionary<string, Known> Curated = new()
    {
        // Battery management system
        ["BMS_a017"] = new(Critical, "Cell group over-voltage", "A cell group went above its maximum safe voltage. The BMS stops or limits charging to protect the pack."),
        ["BMS_a018"] = new(Critical, "Cell group under-voltage", "A cell group dropped below its minimum safe voltage. Power is limited; a deeply discharged cell group can need service."),
        ["BMS_a019"] = new(Critical, "Battery module over-temperature", "A battery module is hotter than its safe limit. Charging and driving power are reduced until it cools."),
        ["BMS_a021"] = new(Warning, "Drive power limit exceeded", "Power drawn while driving went beyond what the BMS allowed at the time."),
        ["BMS_a022"] = new(Critical, "Pack over-current", "Current through the pack exceeded its limit."),
        ["BMS_a023"] = new(Critical, "Pack over-voltage", "Total pack voltage went above its safe maximum."),
        ["BMS_a024"] = new(Critical, "Isolated cell group", "A cell group appears electrically disconnected from the rest of the pack, for example by a failed internal connection."),
        ["BMS_a025"] = new(Warning, "Power balance anomaly", "Power measured at the pack does not match the power accounted for elsewhere in the car."),
        ["BMS_a026"] = new(Warning, "High-frequency current anomaly", "Unexpected fast current fluctuations were measured at the pack."),
        ["BMS_a034"] = new(Warning, "Isolation resistance low (passive check)", "The insulation between the high-voltage system and the car body measured lower than expected. Moisture is a common cause."),
        ["BMS_a035"] = new(Critical, "High-voltage isolation fault", "The insulation between the high-voltage system and the car body is too low. Common causes are moisture or a damaged high-voltage component or cable. Needs service."),
        ["BMS_a036"] = new(Critical, "High-voltage interlock open", "The high-voltage interlock loop, a safety circuit through the high-voltage connectors and covers, is open. High voltage is switched off."),
        ["BMS_a037"] = new(Warning, "Pack flood port open", "The pack's flood port, used by emergency services to flood the pack with water, reports open."),
        ["BMS_a039"] = new(Critical, "High-voltage bus over-voltage", "Voltage on the high-voltage bus (DC link) went above its limit."),
        ["BMS_a041"] = new(Info, "BMS restarted", "The battery management system went through a power-on reset."),
        ["BMS_a042"] = new(Warning, "BMS memory protection error", "The battery management system's processor detected an illegal memory access."),
        ["BMS_a043"] = new(Warning, "BMS watchdog reset", "The battery management system stopped responding and was restarted by its watchdog."),
        ["BMS_a044"] = new(Info, "BMS software assertion", "An internal consistency check in the BMS software failed."),
        ["BMS_a045"] = new(Warning, "BMS software exception", "The BMS software hit an unexpected error."),
        ["BMS_a048"] = new(Info, "BMS log upload requested", "The battery management system asked for its logs to be uploaded to Tesla."),
        ["BMS_a050"] = new(Critical, "Cell voltages missing", "The BMS is not receiving cell group voltage measurements."),
        ["BMS_a052"] = new(Warning, "Charger/DC-DC not responding", "The BMS is not receiving messages from the power conversion system."),
        ["BMS_a053"] = new(Warning, "Thermal model check failed", "The BMS's estimate of pack temperatures disagrees with its sensors."),
        ["BMS_a059"] = new(Warning, "Pack voltage measurement fault", "The pack voltage measurement is faulty or implausible."),
        ["BMS_a060"] = new(Critical, "Isolation self-test failed", "The test that checks insulation between the high-voltage system and the car body failed."),
        ["BMS_a061"] = new(Critical, "Cell group over-voltage (redundant monitor)", "A secondary monitoring circuit detected a cell group above its maximum voltage."),
        ["BMS_a062"] = new(Warning, "Cell voltage imbalance", "Cell group voltages are further apart than expected. Can reduce usable capacity; balancing may correct it over a few charges."),
        ["BMS_a063"] = new(Warning, "Charge port fault (seen by BMS)", "The BMS reports a charge port fault."),
        ["BMS_a064"] = new(Warning, "State-of-charge imbalance", "Cell groups are at noticeably different states of charge. The weakest group limits range; persistent imbalance can need service."),
        ["BMS_a069"] = new(Critical, "Battery energy too low", "Not enough energy is left in the high-voltage battery to keep driving. Charge immediately."),
        ["BMS_a075"] = new(Critical, "Charging could not be disabled", "Charging did not stop when the BMS commanded it to."),
        ["BMS_a076"] = new(Warning, "Discharging while charging", "The pack was discharging while it should have been charging."),
        ["BMS_a077"] = new(Warning, "Charger not regulating", "The charger did not keep current or voltage within the limits the BMS requested."),
        ["BMS_a081"] = new(Warning, "Contactor closing blocked", "The high-voltage contactors were prevented from closing, so high voltage could not be switched on."),
        ["BMS_a082"] = new(Warning, "Contactors forced open", "The high-voltage contactors were opened forcibly, possibly under load."),
        ["BMS_a083"] = new(Critical, "Contactors failed to close", "The high-voltage contactors did not close when commanded."),
        ["BMS_a084"] = new(Info, "Sleep/wake aborted", "The BMS aborted a sleep or wake-up sequence."),
        ["BMS_a088"] = new(Warning, "Front body controller not responding while driving", "The BMS lost messages from the front body controller during a drive."),
        ["BMS_a089"] = new(Warning, "Front body controller not responding", "The BMS is not receiving messages from the front body controller."),
        ["BMS_a090"] = new(Warning, "Gateway not responding", "The BMS is not receiving messages from the gateway."),
        ["BMS_a091"] = new(Warning, "Charge port not responding", "The BMS is not receiving messages from the charge port controller."),
        ["BMS_a094"] = new(Warning, "Drive inverter not responding", "The BMS is not receiving messages from the drive inverter."),
        ["BMS_a099"] = new(Critical, "Module board communication lost", "The BMS lost communication with a module monitoring board, the electronics that measure cell voltages and temperatures."),
        ["BMS_a105"] = new(Warning, "Module temperature sensor fault", "One battery module temperature sensor is faulty."),
        ["BMS_a106"] = new(Critical, "All module temperature sensors faulty", "The BMS cannot read any battery module temperature."),
        ["BMS_a107"] = new(Warning, "Pack voltage readings missing", "The BMS is not receiving its stack voltage measurements."),
        ["BMS_a121"] = new(Warning, "BMS configuration memory error", "The BMS's stored configuration could not be read correctly."),
        ["BMS_a122"] = new(Warning, "BMS temperature reading implausible", "A temperature on the BMS board reads implausibly."),
        ["BMS_a123"] = new(Critical, "Isolation fault inside the pack", "The insulation fault is located inside the high-voltage battery. Needs service."),
        ["BMS_a127"] = new(Warning, "Current sensor data unavailable", "The pack current sensor (shunt) reports no valid value."),
        ["BMS_a128"] = new(Critical, "Current sensor not responding", "The BMS is not receiving data from the pack current sensor (shunt)."),
        ["BMS_a131"] = new(Warning, "Cell balancing circuit failure", "A circuit that bleeds charge from cell groups to balance them has failed."),
        ["BMS_a134"] = new(Info, "Contactor opening delayed", "Opening the high-voltage contactors was postponed."),
        ["BMS_a136"] = new(Warning, "Battery module getting hot", "A battery module is approaching its temperature limit."),
        ["BMS_a137"] = new(Warning, "Cell group voltage low", "A cell group is approaching its minimum voltage."),
        ["BMS_a138"] = new(Warning, "Cell group voltage high", "A cell group is approaching its maximum voltage."),
        ["BMS_a139"] = new(Warning, "High-voltage bus reading implausible", "The high-voltage bus (DC link) voltage reading is implausible."),
        ["BMS_a144"] = new(Warning, "High-voltage controller configuration mismatch", "The high-voltage controller's configuration does not match the BMS's."),
        ["BMS_a145"] = new(Info, "State of charge jumped", "The state-of-charge estimate changed abruptly, usually after a recalibration."),
        ["BMS_a146"] = new(Critical, "Cell group over-discharged", "A cell group was discharged below its safe limit. The pack may need service before it can be charged normally."),
        ["BMS_a151"] = new(Critical, "Isolation fault outside the pack", "The insulation fault is in a high-voltage component or cable outside the battery. Needs service."),
        ["BMS_a159"] = new(Warning, "High-voltage controller error", "The high-voltage controller reported an internal error."),
        ["BMS_a161"] = new(Info, "Drive inverter asked to open contactors", "The drive inverter requested that high voltage be switched off."),
        ["BMS_a162"] = new(Warning, "No power to support 12 V system", "The high-voltage battery cannot currently support the 12 V system."),
        ["BMS_a163"] = new(Critical, "Contactor state mismatch", "The high-voltage contactors are not in the state they were commanded to."),
        ["BMS_a164"] = new(Warning, "Uncontrolled regeneration", "The pack was charged by regenerative braking the BMS had not allowed."),
        ["BMS_a165"] = new(Critical, "Pack contactor partly welded", "A main pack contactor appears partly stuck closed. Needs service."),
        ["BMS_a166"] = new(Critical, "Pack contactor welded", "A main pack contactor is stuck closed. Needs service."),
        ["BMS_a167"] = new(Critical, "Fast-charge contactor partly welded", "A DC fast-charge contactor appears partly stuck closed. Needs service."),
        ["BMS_a168"] = new(Critical, "Fast-charge contactor welded", "A DC fast-charge contactor is stuck closed. Needs service."),
        ["BMS_a169"] = new(Critical, "Fast-charge and pack contactors welded", "Both fast-charge and pack contactors appear stuck closed. Needs service."),
        ["BMS_a170"] = new(Critical, "Battery limp mode", "The battery is in limp mode: driving power is severely limited."),
        ["BMS_a174"] = new(Warning, "Charging failed", "A charging session failed."),
        ["BMS_a176"] = new(Info, "Graceful power-off", "The BMS shut down in an orderly way."),
        ["BMS_a179"] = new(Warning, "High-voltage controller 12 V supply fault", "The 12 V supply to the high-voltage controller is out of range."),

        // High-voltage controller
        ["HVP_w001"] = new(Info, "High-voltage controller watchdog reset", "The high-voltage controller stopped responding and was restarted."),
        ["HVP_w004"] = new(Critical, "Crash event", "The high-voltage controller registered a crash signal."),
        ["HVP_w005"] = new(Critical, "Discharge over-current", "Discharge current exceeded the high-voltage controller's limit."),
        ["HVP_w006"] = new(Critical, "Charge over-current", "Charge current exceeded the high-voltage controller's limit."),
        ["HVP_w007"] = new(Critical, "Over-current", "Pack current exceeded the high-voltage controller's limit."),
        ["HVP_w008"] = new(Critical, "High-voltage controller over-temperature", "The high-voltage controller measured an over-temperature."),
        ["HVP_w009"] = new(Critical, "Over-voltage", "The high-voltage controller measured an over-voltage."),
        ["HVP_w010"] = new(Critical, "Under-voltage", "The high-voltage controller measured an under-voltage."),
        ["HVP_w011"] = new(Critical, "Primary module board not responding", "The primary cell-monitoring board stopped communicating."),
        ["HVP_w012"] = new(Critical, "Secondary module board not responding", "The secondary cell-monitoring board stopped communicating."),
        ["HVP_w013"] = new(Warning, "Module board mismatch", "The cell-monitoring boards report inconsistent data."),
        ["HVP_w024"] = new(Warning, "12 V supply fault", "The high-voltage controller's 12 V supply is out of range."),
        ["HVP_w026"] = new(Critical, "High-voltage interlock fault", "The high-voltage interlock loop is open: a high-voltage connector or cover safety circuit is broken."),
        ["HVP_w028"] = new(Warning, "Pack voltage mismatch", "Pack voltage measurements disagree."),
        ["HVP_w030"] = new(Critical, "Positive contactor arcing", "Arcing was detected at the positive pack contactor."),
        ["HVP_w031"] = new(Critical, "Negative contactor arcing", "Arcing was detected at the negative pack contactor."),
        ["HVP_w033"] = new(Critical, "Fast-charge contactor hardware fault", "A DC fast-charge contactor has a hardware fault."),
        ["HVP_w034"] = new(Critical, "Over-voltage (redundant monitor)", "A secondary monitoring circuit detected an over-voltage."),
        ["HVP_w035"] = new(Critical, "Pack contactor hardware fault", "A main pack contactor has a hardware fault."),
        ["HVP_w036"] = new(Critical, "Pyro fuse blown", "The pyrotechnic fuse that disconnects the battery (e.g. in a crash) has fired. High voltage is permanently cut until it is replaced."),
        ["HVP_w037"] = new(Critical, "Pyro fuse failed to fire", "The pyrotechnic fuse was commanded to fire but did not."),
        ["HVP_w039"] = new(Critical, "Pack contactor fell open", "A main pack contactor opened unexpectedly."),
        ["HVP_w040"] = new(Critical, "Fast-charge contactor fell open", "A DC fast-charge contactor opened unexpectedly."),
        ["HVP_w045"] = new(Critical, "High-voltage bus over-voltage", "Voltage on the high-voltage bus (DC link) went above its limit."),
        ["HVP_w046"] = new(Warning, "Current sensor over-temperature", "The pack current sensor (shunt) is too hot."),
        ["HVP_w047"] = new(Critical, "Passive pyro fuse deployed", "The thermally triggered pyro fuse has deployed."),
        ["HVP_w048"] = new(Info, "High-voltage controller log upload requested", "The high-voltage controller asked for its logs to be uploaded to Tesla."),
        ["HVP_w049"] = new(Critical, "Pack contactors failed to close", "The main pack contactors did not close when commanded."),
        ["HVP_w050"] = new(Critical, "Fast-charge contactors failed to close", "The DC fast-charge contactors did not close when commanded."),

        // Charging: power conversion system, charge port, connectors
        ["PCS_a007"] = new(Warning, "Charger phase hot", "A phase of the on-board charger is running hot; charging power may be reduced."),
        ["PCS_a008"] = new(Warning, "Charger phase over-temperature", "A phase of the on-board charger is over temperature."),
        ["PCS_a016"] = new(Critical, "All charger phases faulted", "Every phase of the on-board charger is faulted; AC charging is not possible."),
        ["PCS_a017"] = new(Info, "Wall power removed", "AC power disappeared during charging (unplugged or power cut)."),
        ["PCS_a019"] = new(Info, "AC charging power limited", "AC charging power is being limited."),
        ["PCS_a027"] = new(Warning, "Charger cooling insufficient", "The on-board charger is not getting enough cooling."),
        ["PCS_a040"] = new(Info, "DC-DC at maximum power", "The DC-DC converter supplying the 12 V system is at its maximum output."),
        ["PCS_a041"] = new(Warning, "DC-DC over-temperature", "The DC-DC converter supplying the 12 V system is over temperature."),
        ["PCS_a050"] = new(Critical, "DC-DC 12 V support failed", "The DC-DC converter stopped supporting the 12 V system. The 12 V battery will run down."),
        ["PCS_a052"] = new(Info, "No AC voltage", "No AC voltage is present at the charger input."),
        ["PCS_a053"] = new(Warning, "Charger input voltage drop high", "Input voltage drops noticeably under load; the supply wiring may be weak."),
        ["PCS_a054"] = new(Warning, "Charger input voltage drop too high", "Input voltage drops too much under load; charging power is reduced. Check the outlet and wiring."),
        ["PCS_a080"] = new(Warning, "Damaged charger phase", "A damaged phase was detected in the on-board charger."),
        ["CP_a009"] = new(Info, "Charge port cover open", "The charge port door is open."),
        ["CP_a054"] = new(Info, "Charge cable not secured", "The charge cable is not latched in the charge port."),
        ["CP_a057"] = new(Warning, "Charging station faulted", "The charging station reported a fault."),
        ["CP_a058"] = new(Warning, "AC charging blocked", "AC charging is currently blocked."),
        ["CP_a064"] = new(Warning, "Supercharging blocked", "DC fast charging is currently blocked."),
        ["CC_a003"] = new(Warning, "Wall Connector ground-fault trip", "The Wall Connector's ground-fault protection tripped."),
        ["CC_a010"] = new(Critical, "Wall Connector contactor welded", "The Wall Connector's internal contactor is stuck closed."),
        ["UMC_a002"] = new(Warning, "Mobile Connector ground-fault trip", "The Mobile Connector's ground-fault protection tripped."),
        ["UMC_a006"] = new(Critical, "Mobile Connector contactor welded", "The Mobile Connector's internal contactor is stuck closed."),
        ["UMC_a008"] = new(Warning, "Wall plug over-temperature", "The Mobile Connector's wall plug is too hot. Check the outlet; charging current is reduced or stopped."),

        // 12 V system and thermal (front body controller)
        ["VCFRONT_a135"] = new(Warning, "Coolant level low", "The coolant level is low. The battery and drive unit share this cooling loop."),
        ["VCFRONT_a182"] = new(Warning, "Replace 12 V battery", "The 12 V battery needs replacing."),
        ["VCFRONT_a183"] = new(Warning, "12 V battery capacity low", "The 12 V battery's capacity is low."),
        ["VCFRONT_a190"] = new(Critical, "DC-DC converter not operating", "The DC-DC converter that charges the 12 V battery is not operating."),
        ["VCFRONT_a196"] = new(Warning, "12 V battery disconnected", "The 12 V battery appears disconnected."),
        ["VCFRONT_a213"] = new(Warning, "12 V battery overcharged", "The 12 V battery is being overcharged."),
    };
}

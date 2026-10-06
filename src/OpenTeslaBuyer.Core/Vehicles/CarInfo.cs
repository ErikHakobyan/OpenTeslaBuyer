using System.Globalization;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Vehicles;

/// <summary>A piece of car configuration shown to the user, e.g. map region or Autopilot hardware.</summary>
public sealed record CarInfoField(string Key, string Label, string Group, string? Hint = null);

/// <summary>
/// Configuration and infotainment details the car broadcasts. On the Model 3 platform the gateway sends its
/// configuration in message 0x7FF (pages selected by the first byte); pre-2021 Model S/X only send a country code.
/// The infotainment software version, map version and update history are not broadcast on the diagnostic bus.
/// </summary>
public static class CarInfo
{
    public const string Infotainment = "Infotainment";
    public const string Vehicle = "Vehicle";

    /// <summary>Display order. Values are stored in <see cref="BatteryData.CarInfo"/> under these keys.</summary>
    public static IReadOnlyList<CarInfoField> Fields { get; } =
    [
        new("country", "Country", Infotainment, "Country the car is configured for."),
        new("mapRegion", "Map region", Infotainment, "Navigation map region installed for."),
        new("connectivity", "Connectivity", Infotainment, "Premium Connectivity adds live traffic, satellite maps and streaming."),
        new("audio", "Audio system", Infotainment),
        new("immersiveAudio", "Immersive audio", Infotainment),
        new("processor", "Touchscreen computer", Infotainment, "Estimated from when the car was built; the car does not broadcast it. " + InfotainmentComputer.HowToCheck),
        new("autopilotHardware", "Autopilot hardware", Infotainment, "The Autopilot computer fitted (HW2.5, HW3, …)."),
        new("autopilot", "Autopilot package", Infotainment, "The driver-assistance package enabled on the car."),
        new("chassis", "Chassis", Vehicle),
        new("drivetrain", "Drivetrain", Vehicle),
        new("performance", "Performance package", Vehicle),
        new("pack", "Battery pack (configured)", Vehicle, "The pack size the gateway is configured for."),
        new("softRange", "Range limit", Vehicle, "Whether the pack is software-limited to less than its full capacity."),
        new("supercharging", "Supercharging", Vehicle, "Whether Supercharging is free, allowed or pay-as-you-go for this car."),
        new("exteriorColor", "Paint", Vehicle),
        new("wheels", "Wheels", Vehicle),
        new("steering", "Steering side", Vehicle),
        new("towPackage", "Tow package", Vehicle),
        new("twelveVBattery", "12 V battery type", Vehicle),
        new("birthday", "Gateway birthday", Vehicle, "A date stored in the gateway's configuration under the name \"birthday\"; usually close to when the car was built (unverified)."),
    ];

    public static string Label(string key) => Fields.FirstOrDefault(f => f.Key == key)?.Label ?? key;

    /// <summary>The decoded details plus those derived from them (the estimated touchscreen computer).</summary>
    public static Dictionary<string, string> WithEstimates(BatteryData data)
    {
        var values = new Dictionary<string, string>(data.CarInfo);
        if (InfotainmentComputer.Estimate(data) is { } computer)
            values["processor"] = $"{computer.Display} (estimated from {computer.Basis})";
        return values;
    }

    /// <summary>What is not available over the diagnostic bus, to show next to the decoded fields.</summary>
    public const string NotBroadcastNote =
        "The infotainment software version, map version and update history are not sent on the diagnostic bus. Read them on the touchscreen under Controls › Software.";

    /// <summary>"Label: value" lines, in display order.</summary>
    public static string ToText(IReadOnlyDictionary<string, string> values) =>
        string.Join(Environment.NewLine, Fields.Where(f => values.ContainsKey(f.Key)).Select(f => $"{f.Label}: {values[f.Key]}"));

    /// <summary>Two ASCII letters as a country, e.g. "United States (US)"; tolerates either byte order.</summary>
    public static string? Country(byte first, byte second)
    {
        if (!char.IsAsciiLetterUpper((char)first) || !char.IsAsciiLetterUpper((char)second))
            return null;

        var code = $"{(char)first}{(char)second}";
        var reversed = $"{(char)second}{(char)first}";
        if (RegionName(code) is { } name)
            return $"{name} ({code})";
        return RegionName(reversed) is { } other ? $"{other} ({reversed})" : code;
    }

    private static string? RegionName(string code)
    {
        try
        {
            return new RegionInfo(code).EnglishName;
        }
        catch (ArgumentException)
        {
            return null;
        }
    }
}

/// <summary>The gateway configuration message (0x7FF) of the Model 3 platform, from joshwardell/model3dbc.</summary>
public static class GatewayConfig
{
    public const uint Id = 0x7FF;

    public static readonly Signal Page = new(0, 8);

    private sealed record Field(string Key, int Page, Signal Signal, Func<ulong, string?> Format);

    private static readonly Field[] Fields =
    [
        new("drivetrain", 1, new(10, 1), v => v == 1 ? "AWD (dual motor)" : "RWD (single motor)"),
        new("steering", 1, new(11, 1), v => v == 1 ? "Right-hand drive" : "Left-hand drive"),
        new("autopilotHardware", 1, new(40, 3), v => v switch { 3 => "HW2.5", 4 => "HW3", _ => $"Code {v}" }),
        new("autopilot", 2, new(42, 3), v => v switch
        {
            0 => "None",
            1 => "Autopilot (highway)",
            2 => "Enhanced Autopilot",
            3 => "Full Self-Driving",
            4 => "Basic Autopilot",
            _ => $"Code {v}",
        }),
        new("supercharging", 2, new(45, 2), v => v switch { 0 => "Not allowed", 1 => "Allowed", 2 => "Pay as you go", _ => null }),
        new("exteriorColor", 2, new(48, 3), v => v switch
        {
            0 => "Red Multi-Coat",
            1 => "Solid Black",
            2 => "Silver Metallic",
            3 => "Midnight Silver Metallic",
            5 => "Deep Blue Metallic",
            6 => "Pearl White Multi-Coat",
            _ => $"Code {v}",
        }),
        new("mapRegion", 3, new(8, 4), v => v switch
        {
            0 => "United States",
            1 => "Europe",
            2 => "None",
            3 => "China",
            4 => "Australia",
            5 => "Japan",
            6 => "Taiwan",
            7 => "Korea",
            8 => "Middle East",
            9 => "Hong Kong",
            10 => "Macau",
            _ => $"Code {v}",
        }),
        new("performance", 3, new(12, 3), v => v switch
        {
            0 => "Base",
            1 => "Performance",
            2 => "Ludicrous",
            3 => "Base Plus",
            4 => "Base Plus AWD",
            _ => $"Code {v}",
        }),
        new("towPackage", 3, new(15, 1), v => v == 1 ? "Yes" : "No"),
        new("chassis", 3, new(18, 3), v => v switch { 0 => "Model S", 1 => "Model X", 2 => "Model 3", 3 => "Model Y", _ => $"Code {v}" }),
        new("connectivity", 3, new(27, 1), v => v == 1 ? "Premium" : "Standard"),
        new("audio", 3, new(30, 2), v => v switch { 0 => "Standard", 1 => "Premium", 2 => "Standard with premium upgrade", _ => null }),
        new("pack", 3, new(32, 5), v => v switch
        {
            0 => "50 kWh",
            1 => "74 kWh",
            2 => "62 kWh",
            3 => "100 kWh",
            4 => "75 kWh",
            _ => $"Code {v}",
        }),
        new("softRange", 3, new(42, 3), v => v switch { 0 => "None (full capacity)", 1 => "Limited to 220 miles", 2 => "Limited to 93 miles", _ => $"Code {v}" }),
        new("wheels", 3, new(48, 7), v => v switch
        {
            0 => "18\" Aero (Pinwheel)",
            1 => "19\" Sport (Stiletto)",
            2 => "20\" Sport (Stiletto)",
            3 => "20\" Überturbine, staggered",
            4 => "19\" Gemini, square",
            5 => "19\" Gemini, staggered",
            14 => "20\" Stiletto dark, square",
            15 => "20\" Induction, black",
            16 => "21\" Überturbine, black",
            17 => "19\" Apollo, silver",
            18 => "18\" Aero with caps",
            19 => "20\" Zero-G, gunpowder",
            20 => "19\" Apollo with caps",
            _ => $"Code {v}",
        }),
        new("immersiveAudio", 3, new(56, 2), v => v switch { 0 => "Off", 1 => "Standard", 2 => "Premium", _ => null }),
        new("twelveVBattery", 3, new(63, 1), v => v == 1 ? "Clarios B24 (flooded lead-acid)" : "Atlas BX B24 (flooded lead-acid)"),
        new("birthday", 4, new(8, 32), Birthday),
    ];

    public static void Decode(byte[] data, Dictionary<string, string> info)
    {
        var page = (int)Page.Raw(data);
        if (page == 1 && data.Length >= 4 && CarInfo.Country(data[2], data[3]) is { } country)
            info["country"] = country;

        foreach (var field in Fields.Where(f => f.Page == page && f.Signal.Fits(data)))
        {
            if (field.Format(field.Signal.Raw(data)) is { } value)
                info[field.Key] = value;
        }
    }

    /// <summary>Builds one page; used by the simulator and tests. Values are the raw codes.</summary>
    public static byte[] Encode(int page, IReadOnlyDictionary<string, ulong> raw, string? country = null)
    {
        var data = new byte[8];
        Page.EncodeRaw(data, (ulong)page);
        if (page == 1 && country is { Length: 2 })
        {
            data[2] = (byte)country[0];
            data[3] = (byte)country[1];
        }

        foreach (var field in Fields.Where(f => f.Page == page && raw.ContainsKey(f.Key)))
            field.Signal.EncodeRaw(data, raw[field.Key]);
        return data;
    }

    private static string? Birthday(ulong seconds)
    {
        var date = DateTimeOffset.FromUnixTimeSeconds((long)seconds);
        return date.Year is >= 2012 and <= 2040 ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    }
}

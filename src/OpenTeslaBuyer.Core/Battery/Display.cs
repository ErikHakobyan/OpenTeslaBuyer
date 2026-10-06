using System.Globalization;

namespace OpenTeslaBuyer.Core.Battery;

/// <summary>Shared number formatting for the UI and the exported report. Missing values show as an em dash.</summary>
public static class Display
{
    public const string Missing = "—";

    private static readonly CultureInfo Culture = CultureInfo.CurrentCulture;

    public static string KWh(double? value, string format = "0.0") => value is { } v ? v.ToString(format, Culture) + " kWh" : Missing;

    public static string Percent(double? value, string format = "0.0") => value is { } v ? v.ToString(format, Culture) + "%" : Missing;

    public static string Volts(double? value, string format = "0.0") => value is { } v ? v.ToString(format, Culture) + " V" : Missing;

    public static string Amps(double? value) => value is { } v ? v.ToString("0.0", Culture) + " A" : Missing;

    public static string Kilowatts(double? value) => value is { } v ? v.ToString("0.0", Culture) + " kW" : Missing;

    public static string Celsius(double? value) => value is { } v ? v.ToString("0.0", Culture) + " °C" : Missing;

    public static string Millivolts(double? value) => value is { } v ? v.ToString("0", Culture) + " mV" : Missing;

    public static string Number(double? value, string format = "0") => value is { } v ? v.ToString(format, Culture) : Missing;

    public static string Distance(double? km, bool miles, string format = "0") =>
        km is { } v ? (miles ? v / Units.KmPerMile : v).ToString(format, Culture) + (miles ? " mi" : " km") : Missing;

    public static string Rating(HealthRating rating) => rating switch
    {
        HealthRating.Excellent => "Excellent",
        HealthRating.Good => "Good",
        HealthRating.Fair => "Fair",
        HealthRating.Poor => "Poor",
        _ => "Waiting for data",
    };

    public static string Source(CapacitySource source) => source switch
    {
        CapacitySource.Bms => "stored in the BMS",
        CapacitySource.Manual => "entered manually",
        CapacitySource.PackChosen => "from the chosen pack",
        CapacitySource.Estimated => "estimated from the pack type",
        _ => "not received yet",
    };
}

using System.Globalization;
using OpenTeslaBuyer.Core.Battery;

namespace OpenTeslaBuyer.Core.Vehicles;

/// <summary>The touchscreen computer (MCU) generation and processor, estimated for a car.</summary>
/// <param name="Display">E.g. "Intel Atom (MCU2)" or "Intel Atom (MCU2) or AMD Ryzen (MCU3)".</param>
/// <param name="Mcu1AsBuilt">True when the car was (or may have been) built with MCU1, which buyers should check.</param>
/// <param name="Certain">False near a hardware switch-over, when either generation is possible.</param>
/// <param name="Basis">What the estimate rests on, e.g. "build date 2018-06-15".</param>
public sealed record InfotainmentEstimate(string Display, bool Mcu1AsBuilt, bool Certain, string Basis);

/// <summary>
/// The car does not broadcast its touchscreen computer type, so it is estimated from when the car was built:
/// Model S/X used MCU1 (Nvidia Tegra 3) until about March 2018, then MCU2 (Intel Atom) until the 2021 refresh,
/// which brought MCU3 (AMD Ryzen); Model 3/Y used MCU2 until AMD Ryzen arrived around late 2021 to mid 2022,
/// depending on the factory. Older S/X are often upgraded from MCU1 to MCU2, which the bus does not reveal.
/// </summary>
public static class InfotainmentComputer
{
    public const string Mcu1 = "Nvidia Tegra 3 (MCU1)";
    public const string Mcu2 = "Intel Atom (MCU2)";
    public const string Mcu3 = "AMD Ryzen (MCU3)";

    private static readonly DateTimeOffset ModelSxMcu2From = new(2018, 3, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Model3RyzenEarliest = new(2021, 10, 1, 0, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset Model3RyzenLatest = new(2022, 7, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>How to confirm on the car itself.</summary>
    public const string HowToCheck =
        "Check Controls › Software › Additional Vehicle Information: MCU2 and later list an \"Infotainment processor\" (Intel Atom or AMD Ryzen); MCU1 cars have no such line.";

    public static InfotainmentEstimate? Estimate(BatteryData data)
    {
        if (data.Vin is not { } vinText || new VinInfo(vinText) is not { ModelYear: { } year } vin)
            return null;

        var built = BuildDate(data);
        var basis = built is { } date ? $"build date {date:yyyy-MM-dd}" : $"model year {year}";

        switch (vin.Model)
        {
            case TeslaModel.ModelS or TeslaModel.ModelX when data.Platform == VehiclePlatform.Model3Family:
                return new(Mcu3, false, true, "2021+ refresh");

            case TeslaModel.ModelS or TeslaModel.ModelX:
                if (built is { } b ? b < ModelSxMcu2From : year <= 2017)
                    return new($"{Mcu1} as built; may have been upgraded to MCU2", true, true, basis);
                if (built is null && year == 2018)
                    return new($"{Mcu1} or {Mcu2} (changed around March 2018)", true, false, basis);
                return new(Mcu2, false, true, basis);

            case TeslaModel.Model3 or TeslaModel.ModelY:
                if (built is { } d)
                {
                    if (d < Model3RyzenEarliest)
                        return new(Mcu2, false, true, basis);
                    if (d >= Model3RyzenLatest)
                        return new(Mcu3, false, true, basis);
                    return new($"{Mcu2} or {Mcu3} (changed between late 2021 and mid 2022)", false, false, basis);
                }

                return year switch
                {
                    <= 2021 => new(Mcu2, false, true, basis),
                    2022 => new($"{Mcu2} or {Mcu3} (changed during the 2022 model year)", false, false, basis),
                    _ => new(Mcu3, false, true, basis),
                };

            default:
                return null;
        }
    }

    /// <summary>The gateway's "birthday" (Model 3 platform), a good proxy for when the car was built.</summary>
    private static DateTimeOffset? BuildDate(BatteryData data) =>
        data.CarInfo.TryGetValue("birthday", out var text)
        && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var date)
            ? date
            : null;
}

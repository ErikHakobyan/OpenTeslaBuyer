using System.Globalization;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Battery;

/// <summary>
/// A pre-2021 Model S/X battery pack and its capacity when new, as the car's BMS reports it ("nominal full pack",
/// buffer included). Tesla does not publish these; they are typical as-new readings from owners' diagnostic tools,
/// so each carries a tolerance.
/// </summary>
/// <param name="CellGroups">Cell groups (bricks) in series: the early 14-module 60 kWh pack has 84, all others 96.</param>
/// <param name="Basis">Where the as-new value comes from, shown to the user.</param>
public sealed record PackType(
    string Key,
    string Name,
    double NewKWh,
    double TolerancePercent,
    int CellGroups,
    (int First, int Last) ModelSYears,
    (int First, int Last)? ModelXYears,
    string Basis)
{
    public bool OfferedFor(TeslaModel model, int year) => model switch
    {
        TeslaModel.ModelS => year >= ModelSYears.First && year <= ModelSYears.Last,
        TeslaModel.ModelX => ModelXYears is { } x && year >= x.First && year <= x.Last,
        _ => false,
    };
}

/// <summary>The likeliest pack for a car, with the alternatives that also fit.</summary>
/// <param name="ExpectedHealth">State of health expected from mileage (or age), as a fraction.</param>
/// <param name="Confident">False when another pack fits almost as well; the user should confirm.</param>
public sealed record PackEstimate(
    PackType Pack,
    double Health,
    double ExpectedHealth,
    bool Confident,
    IReadOnlyList<PackType> Alternatives,
    string Reason);

/// <summary>
/// Pre-2021 Model S/X do not report their capacity when new, so it is estimated: the packs offered for the car's
/// model and year (narrowed by the early VIN battery code and the number of cell groups) are compared with today's
/// capacity, and the pack whose implied wear best matches the wear expected for the car's mileage wins. Expected
/// wear follows Tesla's report that Model S/X packs keep about 88% after 200,000 miles, most of it lost early.
/// </summary>
public static class PackEstimator
{
    private const double KmAt200kMiles = 321_869;
    private const double EarlyLoss = 0.05;
    private const double EarlyKm = 50_000;
    private const double LossAt200kMiles = 0.12;
    private const double KmPerYear = 20_000;
    private const double MinHealth = 0.68;
    private const double MaxHealth = 1.04;
    private const double ClearMargin = 0.05;

    public static IReadOnlyList<PackType> Packs { get; } =
    [
        new("60-14", "60 kWh (14 modules, 2012–2015)", 59.0, 5, 84, (2012, 2015), null,
            "Estimated from the pack's cell count relative to the 85 kWh pack; few owner readings."),
        new("70", "70 kWh (2015–2016)", 68.5, 5, 96, (2015, 2016), (2016, 2016),
            "Estimated; few owner readings."),
        new("75", "75 kWh (2016–2019, incl. software-limited 60/60D)", 75.0, 4, 96, (2016, 2019), (2016, 2019),
            "Owner readings of about 70–72 kWh after a year of use."),
        new("85", "85 kWh (2012–2016)", 81.5, 2, 96, (2012, 2016), null,
            "Widely reported as-new reading; matches pack teardowns (about 81 kWh)."),
        new("90", "90 kWh (2015–2017)", 85.8, 2, 96, (2015, 2017), (2016, 2017),
            "Commonly reported as-new reading."),
        new("100", "100 kWh (2016–2021)", 98.5, 3, 96, (2016, 2021), (2016, 2021),
            "New cars have read 96.9–102.4 kWh."),
    ];

    public static PackType? Find(string key) => Packs.FirstOrDefault(p => p.Key == key);

    /// <summary>Wear expected after <paramref name="km"/>, as a fraction of capacity (0.12 = 12% lost).</summary>
    public static double ExpectedLoss(double km) =>
        Math.Min(0.25, EarlyLoss * Math.Min(km, EarlyKm) / EarlyKm
                       + (LossAt200kMiles - EarlyLoss) * Math.Max(0, km - EarlyKm) / (KmAt200kMiles - EarlyKm));

    public static PackEstimate? Estimate(BatteryData data, int? currentYear = null)
    {
        if (data.NominalFullPackKWh is not { } current || data.Vin is not { } vinText)
            return null;

        var vin = new VinInfo(vinText);
        if (vin.Model is not (TeslaModel.ModelS or TeslaModel.ModelX) || vin.ModelYear is not { } year)
            return null;

        var packKm = data.BatteryOdometerKm ?? data.OdometerKm;
        var km = packKm ?? Math.Max(0, (currentYear ?? DateTime.UtcNow.Year) - year) * KmPerYear;
        var expected = 1 - ExpectedLoss(km);

        var candidates = Packs.Where(p => p.OfferedFor(vin.Model, year)).ToList();
        var reasons = new List<string> { $"{year} {(vin.Model == TeslaModel.ModelS ? "Model S" : "Model X")}" };

        // Early Model S VINs named the pack in digit 7 (H = 85 kWh, S = 60 kWh).
        var batteryCode = vin.Vin.Length > 6 ? vin.Vin[6] : ' ';
        if (vin.Model == TeslaModel.ModelS && batteryCode is 'H' or 'S')
        {
            candidates = candidates.Where(p => p.Key == (batteryCode == 'H' ? "85" : "60-14")).ToList();
            reasons.Add($"VIN battery code {batteryCode}");
        }

        if (data.CellGroupCount is { } groups)
        {
            candidates = candidates.Where(p => p.CellGroups == groups).ToList();
            reasons.Add($"{groups} cell groups");
        }

        reasons.Add(packKm is { } odometer
            ? $"{odometer.ToString("N0", CultureInfo.CurrentCulture)} km{(data.BatteryOdometerKm is null ? "" : " on the pack")}"
            : $"about {km.ToString("N0", CultureInfo.CurrentCulture)} km assumed for its age");
        reasons.Add($"{current.ToString("0.0", CultureInfo.CurrentCulture)} kWh today");

        // A replacement pack can be newer than the car; if nothing fits, consider every pack with the right cell count.
        var fitting = Fit(candidates, current);
        if (fitting.Count == 0)
            fitting = Fit(Packs.Where(p => data.CellGroupCount is not { } g || p.CellGroups == g), current);
        if (fitting.Count == 0)
            return null;

        var ranked = fitting.OrderBy(f => Math.Abs(f.Health - expected)).ToList();
        var best = ranked[0];
        var confident = ranked.Count == 1 || Math.Abs(ranked[1].Health - expected) - Math.Abs(best.Health - expected) >= ClearMargin;
        return new PackEstimate(best.Pack, best.Health, expected, confident, ranked.Skip(1).Select(f => f.Pack).ToList(), string.Join(", ", reasons));
    }

    private static List<(PackType Pack, double Health)> Fit(IEnumerable<PackType> packs, double current) =>
        packs.Select(p => (Pack: p, Health: current / p.NewKWh))
            .Where(f => f.Health is >= MinHealth and <= MaxHealth)
            .ToList();
}

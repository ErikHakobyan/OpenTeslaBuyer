using System.Globalization;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Buyer;

public enum CheckStatus
{
    Pass,
    Attention,
    Fail,
    NotAvailable,
}

/// <summary>One line of the buyer check.</summary>
/// <param name="Detail">How the verdict was reached and what to do about it.</param>
public sealed record CheckItem(string Key, string Title, CheckStatus Status, string Summary, string? Detail = null);

/// <summary>Everything the buyer check looks at, so it can be evaluated anywhere (UI, report, tests).</summary>
public sealed record CheckInput(
    BatteryData Data,
    HealthReport Health,
    IReadOnlyList<ActiveAlert> CurrentAlerts,
    IReadOnlyList<AlertHistoryEntry> AlertHistory,
    AlertCoverage AlertCoverage,
    DateTimeOffset Now);

/// <summary>
/// The things a used-car buyer cannot easily find out any other way, each rated pass / attention / fail.
/// Thresholds are stated rules of thumb, not Tesla's criteria; every item explains what it is based on.
/// </summary>
public static class BuyerCheck
{
    /// <summary>Alerts that should stop a purchase until explained: crash, pyro fuse, welded contactors, isolation and similar.</summary>
    public static IReadOnlySet<string> DealBreakers { get; } = new HashSet<string>
    {
        "RCM_a000", "RCM_a001", "HVP_w004", "TAS_w025",
        "HVP_w036", "HVP_w037", "HVP_w047",
        "BMS_a165", "BMS_a166", "BMS_a167", "BMS_a168", "BMS_a169",
        "BMS_a035", "BMS_a123", "BMS_a151", "BMS_a060",
        "BMS_a024", "BMS_a146", "BMS_a170", "BMS_a037", "BMS_a036", "HVP_w026",
    };

    /// <summary>Alerts about the 12 V battery or its supply.</summary>
    public static IReadOnlySet<string> TwelveVoltAlerts { get; } = new HashSet<string>
    {
        "VCFRONT_a182", "VCFRONT_a183", "VCFRONT_a190", "VCFRONT_a191", "VCFRONT_a196", "VCFRONT_a213",
        "VCFRONT_a219", "VCFRONT_a243", "PCS_a015", "PCS_a050", "HVP_w024", "BMS_a179",
    };

    private const double AtRestAmps = 5;
    private const double MinOhmsPerVolt = 500;

    public static IReadOnlyList<CheckItem> Evaluate(CheckInput input) =>
    [
        BatteryHealth(input),
        RedFlags(input),
        PackHistory(input),
        Isolation(input),
        Charging(input),
        Warranty(input),
        TwelveVolt(input),
        CellBalance(input),
        Infotainment(input),
    ];

    /// <summary>E.g. "1 fail, 2 attention, 4 pass".</summary>
    public static string Summarize(IReadOnlyList<CheckItem> items)
    {
        var parts = new[] { CheckStatus.Fail, CheckStatus.Attention, CheckStatus.Pass }
            .Select(s => (Status: s, Count: items.Count(i => i.Status == s)))
            .Where(p => p.Count > 0)
            .Select(p => $"{p.Count} {Describe(p.Status).ToLowerInvariant()}");
        return string.Join(", ", parts);
    }

    /// <summary>The worst status among items that were judged; not available when none was.</summary>
    public static CheckStatus Overall(IReadOnlyList<CheckItem> items) =>
        items.Any(i => i.Status == CheckStatus.Fail) ? CheckStatus.Fail
        : items.Any(i => i.Status == CheckStatus.Attention) ? CheckStatus.Attention
        : items.Any(i => i.Status == CheckStatus.Pass) ? CheckStatus.Pass
        : CheckStatus.NotAvailable;

    public static string Describe(CheckStatus status) => status switch
    {
        CheckStatus.Pass => "Pass",
        CheckStatus.Attention => "Attention",
        CheckStatus.Fail => "Fail",
        _ => "Not available",
    };

    private static CheckItem BatteryHealth(CheckInput input)
    {
        const string key = "health", title = "Battery health";
        var health = input.Health;
        if (health.StateOfHealthPercent is not { } soh)
        {
            return new(key, title, CheckStatus.NotAvailable, health.CurrentKWh is null
                ? "Waiting for the battery's capacity."
                : "The pack's original capacity is unknown, so health cannot be rated.");
        }

        var status = soh >= 80 ? CheckStatus.Pass : soh >= HealthCalculator.WarrantyRetentionPercent ? CheckStatus.Attention : CheckStatus.Fail;
        var summary = $"{Pct(soh)} state of health ({Pct(health.DegradationPercent ?? 0)} capacity lost).";
        var detail = $"{Display.KWh(health.CurrentKWh)} now versus {Display.KWh(health.OriginalKWh)} when new ({ReportWriter.OriginLabel(health)}). "
                     + "80% or more passes; below 70% is under the capacity Tesla's battery warranty guarantees.";
        if (health.OriginalSource == CapacitySource.Estimated && health.Pack is { } pack)
            detail += $" The original capacity is estimated (±{pack.TolerancePercent:0}%), so treat the rating as approximate.";
        return new(key, title, status, summary, detail);
    }

    private static CheckItem RedFlags(CheckInput input)
    {
        const string key = "redFlags", title = "Serious alerts";
        var coverage = input.AlertCoverage;
        if (!coverage.Supported)
        {
            return new(key, title, CheckStatus.NotAvailable,
                "This car's alert messages are not decoded, so crash, pyro-fuse and contactor events cannot be checked here.",
                "Pre-2021 Model S/X alert messages are not publicly decoded. Ask for the service history, or have Tesla read the alert log.");
        }

        if (coverage.MessagesReceived == 0)
            return new(key, title, CheckStatus.NotAvailable, "Waiting for the car's alert messages.");

        var seen = input.CurrentAlerts.Select(a => (a.Definition, Active: true))
            .Concat(input.AlertHistory.Where(h => !h.ActiveNow).Select(h => (h.Definition, Active: false)))
            .ToList();
        var dealBreakers = seen.Where(s => DealBreakers.Contains(s.Definition.Code)).ToList();
        var critical = seen.Where(s => !DealBreakers.Contains(s.Definition.Code) && s.Definition.Severity == AlertSeverity.Critical).ToList();
        var basis = $"Based on alerts active now or recorded by this tool before, from {coverage.MessagesReceived} of {coverage.MessagesKnown} controllers. "
                    + "Events the car logged and Tesla cleared before this tool connected are not visible.";

        if (dealBreakers.Count > 0)
            return new(key, title, CheckStatus.Fail, $"{dealBreakers.Count} serious event(s): {List(dealBreakers)}.", "Ask for an explanation and repair records before buying. " + basis);
        if (critical.Count > 0)
            return new(key, title, CheckStatus.Attention, $"{critical.Count} critical alert(s): {List(critical)}.", basis);
        return new(key, title, CheckStatus.Pass, "No crash, pyro-fuse, contactor, isolation or other critical alerts seen.", basis);

        static string List(IEnumerable<(AlertDefinition Definition, bool Active)> alerts) =>
            string.Join("; ", alerts.Select(a => $"{a.Definition.Title} ({a.Definition.Code}{(a.Active ? ", active now" : ", seen before")})"));
    }

    private static CheckItem PackHistory(CheckInput input)
    {
        const string key = "packHistory", title = "Pack and mileage history";
        var data = input.Data;
        var findings = new List<(CheckStatus Status, string Text)>();

        if (data.BatteryOdometerKm is { } packKm && data.OdometerKm is { } carKm)
        {
            var tolerance = Math.Max(1_000, carKm * 0.02);
            if (carKm - packKm > tolerance)
            {
                findings.Add((CheckStatus.Attention,
                    $"The pack has {Km(packKm)}, the car {Km(carKm)}: the pack was probably replaced at around {Km(carKm - packKm)}. Ask for the replacement invoice; replacement packs have their own warranty."));
            }
            else if (packKm - carKm > tolerance)
            {
                findings.Add((CheckStatus.Attention,
                    $"The pack has more distance ({Km(packKm)}) than the car ({Km(carKm)}): a used pack was fitted, or the odometer was changed."));
            }
            else
            {
                findings.Add((CheckStatus.Pass, $"Battery and car odometers agree ({Km(carKm)}): most likely the original pack."));
            }
        }

        // Energy used per km over the pack's life. Driving-only energy is the tighter measure where the car reports it.
        var km = data.BatteryOdometerKm ?? data.OdometerKm;
        var (energy, low, high, what) = data.DriveDischargeTotalKWh is { } drive
            ? (drive, 90.0, 300.0, "driving energy")
            : (data.DischargeTotalKWh ?? 0, 100.0, 400.0, "energy drawn from the pack");
        if (km is > 2_000 && energy > 0)
        {
            var whPerKm = energy * 1000 / km.Value;
            var rate = $"{whPerKm:0} Wh/km";
            if (whPerKm < low)
            {
                findings.Add((CheckStatus.Attention,
                    $"The battery's lifetime {what} is low for the distance ({rate}): the pack or its battery controller was probably replaced."));
            }
            else if (whPerKm > high)
            {
                findings.Add((CheckStatus.Attention,
                    $"The battery's lifetime {what} is high for the distance ({rate}): check that the odometer reading is genuine."));
            }
            else
            {
                findings.Add((CheckStatus.Pass, $"Lifetime {what} matches the distance ({rate})."));
            }
        }

        if (findings.Count == 0)
            return new(key, title, CheckStatus.NotAvailable, "Waiting for the odometer and the battery's lifetime energy counters.");

        var worst = findings.Max(f => f.Status);
        var lead = findings.First(f => f.Status == worst).Text;
        var rest = findings.Where(f => f.Text != lead).Select(f => f.Text);
        return new(key, title, worst, lead, string.Join(" ", rest.Append("Plausible ranges are rules of thumb: 90–300 Wh/km for driving energy, 100–400 Wh/km for total energy.")));
    }

    private static CheckItem Isolation(CheckInput input)
    {
        const string key = "isolation", title = "High-voltage isolation";
        if (input.Data.IsolationResistanceKOhm is not { } kOhm)
        {
            return new(key, title, CheckStatus.NotAvailable, input.Data.Platform == VehiclePlatform.LegacyModelSX
                ? "Not broadcast by pre-2021 Model S/X."
                : "Waiting for the battery's isolation measurement.");
        }

        var volts = input.Data.PackVoltage ?? 400;
        var minimum = MinOhmsPerVolt * volts / 1000;
        var reading = kOhm >= 1000 ? $"{kOhm / 1000:0.0} MΩ" : $"{kOhm:0} kΩ";
        var basis = $"Insulation between the high-voltage system and the car body. Healthy, dry systems usually read well above 1 MΩ; {MinOhmsPerVolt:0} Ω per volt ({minimum:0} kΩ at {volts:0} V) is used here as the lower limit. Readings drop in wet weather.";
        if (kOhm < minimum)
            return new(key, title, CheckStatus.Fail, $"{reading}: too low. Points to moisture or a damaged high-voltage component; have it inspected.", basis);
        if (kOhm < 1000)
            return new(key, title, CheckStatus.Attention, $"{reading}: lower than usual. Re-check in dry weather; if it stays low, have the high-voltage system inspected.", basis);
        return new(key, title, CheckStatus.Pass, $"{reading}: normal.", basis);
    }

    private static CheckItem Charging(CheckInput input)
    {
        const string key = "charging", title = "Fast-charging share";
        if (input.Data.AcChargeTotalKWh is not { } ac || input.Data.DcChargeTotalKWh is not { } dc || ac + dc <= 0)
        {
            return new(key, title, CheckStatus.NotAvailable, input.Data.Platform == VehiclePlatform.LegacyModelSX
                ? "Pre-2021 Model S/X do not broadcast AC and DC charging totals."
                : "Waiting for the battery's charging counters.");
        }

        var share = dc / (ac + dc) * 100;
        var amounts = $"{dc.ToString("N0", CultureInfo.CurrentCulture)} kWh fast-charged, {ac.ToString("N0", CultureInfo.CurrentCulture)} kWh on AC";
        var detail = $"{amounts}"
                     + (input.Data.RegenChargeTotalKWh is { } regen ? $"; {regen.ToString("N0", CultureInfo.CurrentCulture)} kWh recovered by regenerative braking" : "")
                     + ". Frequent DC fast charging tends to wear a pack somewhat faster; it is normal for long-distance drivers.";
        return share switch
        {
            > 80 => new(key, title, CheckStatus.Attention, $"{share:0}% of charging was fast charging: almost always fast-charged, typical of ride-share or fleet use.", detail),
            > 50 => new(key, title, CheckStatus.Attention, $"{share:0}% of charging was fast charging: more than usual.", detail),
            _ => new(key, title, CheckStatus.Pass, $"{share:0}% of charging was fast charging; mostly charged on AC.", detail),
        };
    }

    private static CheckItem Warranty(CheckInput input)
    {
        const string key = "warranty", title = "Battery warranty (estimate)";
        var data = input.Data;
        if (data.Vin is not { } vinText || new VinInfo(vinText) is not { ModelYear: { } year } vin)
            return new(key, title, CheckStatus.NotAvailable, "Waiting for the VIN.");

        int? miles;
        string terms;
        switch (vin.Model)
        {
            case TeslaModel.Model3 or TeslaModel.ModelY:
                var longRange = (data.InitialFullPackKWh ?? input.Health.OriginalKWh ?? 0) >= 70;
                miles = longRange ? 120_000 : 100_000;
                terms = $"8 years or {miles:N0} miles ({(longRange ? "Long Range / Performance" : "Standard Range")}), with at least 70% capacity retained";
                break;
            case TeslaModel.ModelS or TeslaModel.ModelX when data.Platform == VehiclePlatform.Model3Family || year >= 2020:
                miles = 150_000;
                terms = "8 years or 150,000 miles, with at least 70% capacity retained";
                break;
            case TeslaModel.ModelS or TeslaModel.ModelX:
                miles = data.CellGroupCount == 84 ? 125_000 : null;
                terms = miles is null ? "8 years, unlimited miles" : "8 years or 125,000 miles (60 kWh pack)";
                break;
            default:
                return new(key, title, CheckStatus.NotAvailable, "No warranty rule for this model.");
        }

        var (start, startNote) = data.CarInfo.TryGetValue("birthday", out var birthday)
                                 && DateTimeOffset.TryParse(birthday, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var built)
            ? (built, "from the gateway's build date")
            : (new DateTimeOffset(year, 1, 1, 0, 0, 0, TimeSpan.Zero), $"assuming delivery early in model year {year}");
        var end = start.AddYears(8);
        var usedMiles = data.OdometerKm is { } odometer ? odometer / Units.KmPerMile : (double?)null;

        var detail = $"Tesla's US terms for this car: {terms}. Dates are estimated {startNote}; the real warranty starts at delivery, terms differ by region, and replacement packs carry their own warranty. Confirm with Tesla.";
        var until = end.ToString("MMM yyyy", CultureInfo.CurrentCulture);
        if (input.Now > end)
            return new(key, title, CheckStatus.Attention, $"Likely ended around {until} (8 years).", detail);
        if (miles is { } limit && usedMiles > limit)
            return new(key, title, CheckStatus.Attention, $"Likely ended: {usedMiles.Value.ToString("N0", CultureInfo.CurrentCulture)} miles is over the {limit:N0}-mile limit.", detail);

        var milesLeft = miles is { } cap && usedMiles is { } used ? $" or {(cap - used).ToString("N0", CultureInfo.CurrentCulture)} more miles" : "";
        var endsSoon = end - input.Now < TimeSpan.FromDays(365) || (miles is { } l && usedMiles > l * 0.9);
        return new(key, title, endsSoon ? CheckStatus.Attention : CheckStatus.Pass,
            $"Likely active until about {until}{milesLeft}{(endsSoon ? " — ends soon" : "")}.", detail);
    }

    private static CheckItem TwelveVolt(CheckInput input)
    {
        const string key = "twelveVolt", title = "12 V system";
        var alerts = input.CurrentAlerts.Select(a => (a.Definition, Active: true))
            .Concat(input.AlertHistory.Where(h => !h.ActiveNow).Select(h => (h.Definition, Active: false)))
            .Where(a => TwelveVoltAlerts.Contains(a.Definition.Code))
            .ToList();
        var volts = input.Data.TwelveVoltVolts;
        var reading = volts is { } v ? $"{v:0.0} V with the car awake" : null;
        const string basis = "With the car awake the DC-DC converter supplies 12 V (about 13–15 V), so the voltage shows whether that support works rather than the 12 V battery's own condition. The car's 12 V alerts cover the battery.";

        if (alerts.Count > 0)
        {
            var list = string.Join("; ", alerts.Select(a => $"{a.Definition.Title} ({a.Definition.Code}{(a.Active ? ", active now" : ", seen before")})"));
            return new(key, title, CheckStatus.Attention, $"12 V alerts: {list}.", (reading is null ? "" : reading + ". ") + basis);
        }

        if (volts is { } awake)
        {
            if (awake < 12.4)
                return new(key, title, CheckStatus.Attention, $"{reading}: low. The DC-DC converter may not be supporting the 12 V system.", basis);
            if (awake > 15.2)
                return new(key, title, CheckStatus.Attention, $"{reading}: high.", basis);
            return new(key, title, CheckStatus.Pass, $"{reading}; no 12 V alerts.", basis);
        }

        return input.AlertCoverage.Supported && input.AlertCoverage.MessagesReceived > 0
            ? new(key, title, CheckStatus.Pass, "No 12 V alerts.", basis)
            : new(key, title, CheckStatus.NotAvailable, input.Data.Platform == VehiclePlatform.LegacyModelSX
                ? "Not available on pre-2021 Model S/X; test the 12 V battery separately."
                : "Waiting for data.");
    }

    private static CheckItem CellBalance(CheckInput input)
    {
        const string key = "cellBalance", title = "Cell balance";
        if (input.Health.CellSpreadMv is not { } spread)
            return new(key, title, CheckStatus.NotAvailable, "Waiting for cell voltages.");
        if (input.Data.PackCurrent is { } amps && Math.Abs(amps) > AtRestAmps)
            return new(key, title, CheckStatus.NotAvailable, "The pack is under load; check with the car parked and not charging.");

        const string basis = "Difference between the highest and lowest cell group with the car at rest. Balanced packs usually stay below 20 mV; above 50 mV points to a weak cell group.";
        return spread switch
        {
            > 50 => new(key, title, CheckStatus.Fail, $"{spread:0} mV spread: a weak cell group is likely.", basis),
            > 20 => new(key, title, CheckStatus.Attention, $"{spread:0} mV spread: higher than usual. Re-check after a full charge and a rest.", basis),
            _ => new(key, title, CheckStatus.Pass, $"{spread:0} mV spread.", basis),
        };
    }

    private static CheckItem Infotainment(CheckInput input)
    {
        const string key = "infotainment", title = "Touchscreen computer";
        if (InfotainmentComputer.Estimate(input.Data) is not { } computer)
            return new(key, title, CheckStatus.NotAvailable, "Waiting for the VIN.");

        var detail = $"Estimated from the {computer.Basis}; the car does not broadcast its computer type. {InfotainmentComputer.HowToCheck}";
        if (computer.Mcu1AsBuilt)
        {
            return new(key, title, CheckStatus.Attention,
                $"{computer.Display}. Unless upgraded, expect a slow screen, missing features and MCU1's known memory-chip failure; an upgrade to MCU2 costs extra.",
                detail);
        }

        return new(key, title, CheckStatus.Pass, $"{computer.Display}.", detail);
    }

    private static string Pct(double value) => value.ToString("0.0", CultureInfo.CurrentCulture) + "%";

    private static string Km(double km) => km.ToString("N0", CultureInfo.CurrentCulture) + " km";
}

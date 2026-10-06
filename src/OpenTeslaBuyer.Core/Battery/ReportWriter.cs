using System.Globalization;
using System.Net;
using System.Text;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Buyer;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Battery;

/// <summary>A self-contained HTML battery report; open it in a browser and print to PDF if a PDF is needed.</summary>
public static class ReportWriter
{
    public static string ToHtml(
        BatteryData data,
        HealthReport health,
        bool miles,
        DateTimeOffset generatedAt,
        IReadOnlyList<ActiveAlert>? currentAlerts = null,
        IReadOnlyList<AlertHistoryEntry>? alertHistory = null,
        AlertCoverage? alertCoverage = null,
        IReadOnlyList<CheckItem>? buyerCheck = null,
        ChargeTestResult? chargeTest = null)
    {
        var vin = data.Vin is { } v ? new VinInfo(v) : null;
        var html = new StringBuilder();

        html.Append("""
            <!doctype html>
            <html lang="en"><head><meta charset="utf-8">
            <meta name="viewport" content="width=device-width, initial-scale=1">
            <title>Battery health report</title>
            <style>
              body { font-family: "Segoe UI", system-ui, sans-serif; color: #1b1b1f; background: #fff; margin: 0; padding: 32px 16px; }
              main { max-width: 760px; margin: 0 auto; }
              h1 { font-size: 24px; margin: 0 0 4px; }
              h2 { font-size: 16px; margin: 28px 0 8px; }
              .muted { color: #5f6368; font-size: 13px; }
              .hero { display: flex; gap: 24px; align-items: baseline; margin-top: 20px; padding: 20px; border: 1px solid #dadce0; border-radius: 8px; }
              .hero .big { font-size: 44px; font-weight: 600; }
              table { width: 100%; border-collapse: collapse; font-size: 14px; }
              td { padding: 6px 0; border-bottom: 1px solid #eee; }
              td:last-child { text-align: right; font-variant-numeric: tabular-nums; }
              li { margin: 4px 0; font-size: 14px; }
              .alert { padding: 8px 0 8px 12px; border-left: 4px solid #dadce0; margin: 8px 0; }
              .alert.Critical { border-color: #c5221f; } .alert.Warning { border-color: #e37400; } .alert.Info { border-color: #1a73e8; }
              .alert b { font-size: 14px; } .alert code { color: #5f6368; margin-left: 6px; }
              .alert div { font-size: 13px; margin-top: 2px; }
              .check { display: grid; grid-template-columns: 96px 1fr; gap: 4px 12px; padding: 8px 0; border-bottom: 1px solid #eee; font-size: 14px; }
              .badge { font-size: 12px; font-weight: 600; padding: 2px 8px; border-radius: 10px; text-align: center; align-self: start; }
              .Pass { background: #e6f4ea; color: #137333; } .Attention { background: #fef7e0; color: #b06000; }
              .Fail { background: #fce8e6; color: #c5221f; } .NotAvailable { background: #f1f3f4; color: #5f6368; }
              @media print { body { padding: 0; } }
            </style></head><body><main>
            """);

        html.Append("<h1>Battery health report</h1>");
        html.Append(CultureInfo.CurrentCulture, $"<div class=\"muted\">Generated {Encode(generatedAt.ToLocalTime().ToString("f", CultureInfo.CurrentCulture))}</div>");

        html.Append("<div class=\"hero\">");
        html.Append(CultureInfo.CurrentCulture, $"<div><div class=\"big\">{Display.Percent(health.StateOfHealthPercent)}</div><div class=\"muted\">state of health</div></div>");
        html.Append(CultureInfo.CurrentCulture, $"<div><div class=\"big\">{Display.Percent(health.DegradationPercent)}</div><div class=\"muted\">degradation</div></div>");
        html.Append(CultureInfo.CurrentCulture, $"<div><div class=\"big\">{Encode(Display.Rating(health.Rating))}</div><div class=\"muted\">rating</div></div>");
        html.Append("</div>");

        if (buyerCheck is { Count: > 0 })
        {
            html.Append(CultureInfo.InvariantCulture, $"<h2>Buyer check: {Encode(BuyerCheck.Summarize(buyerCheck))}</h2>");
            foreach (var item in buyerCheck)
            {
                html.Append(CultureInfo.InvariantCulture, $"<div class=\"check\"><span class=\"badge {item.Status}\">{Encode(BuyerCheck.Describe(item.Status))}</span>");
                html.Append(CultureInfo.InvariantCulture, $"<div><b>{Encode(item.Title)}</b>: {Encode(item.Summary)}");
                if (item.Detail is { } detail)
                    html.Append(CultureInfo.InvariantCulture, $"<div class=\"muted\">{Encode(detail)}</div>");
                html.Append("</div></div>");
            }

            html.Append("<p class=\"muted\">Also check by hand: touchscreen responsiveness (and on older S/X whether the MCU1 computer was upgraded), "
                        + "door handles, air suspension holding its height overnight, drive-unit whine at 30–60 km/h, charge-port latch, "
                        + "water marks in the trunk and frunk, uneven tyre wear, open recalls, and whether FSD and free Supercharging transfer to you.</p>");
        }

        Section(html, "Vehicle",
        [
            ("VIN", data.Vin ?? Display.Missing),
            ("Model", vin is null ? Display.Missing : ModelName(vin.Model)),
            ("Model year", vin?.ModelYear?.ToString(CultureInfo.InvariantCulture) ?? Display.Missing),
            ("Plant", vin?.Plant ?? Display.Missing),
            ("Odometer", Display.Distance(data.OdometerKm, miles, "N0")),
        ]);

        Section(html, "Capacity",
        [
            ($"Original capacity ({OriginLabel(health)})", Display.KWh(health.OriginalKWh)),
            ("Current full-pack capacity", Display.KWh(health.CurrentKWh)),
            ("Capacity lost", Display.KWh(health.LostKWh)),
            ("Buffer (below 0%)", Display.KWh(data.EnergyBufferKWh)),
            ("Usable at 100%", Display.KWh(health.UsableKWh)),
            ("Rated range at 100%", Display.Distance(health.FullRangeKm, miles)),
        ]);

        Section(html, "Battery now",
        [
            ("State of charge", Display.Percent(data.SocUiPercent)),
            ("Usable energy remaining", Display.KWh(health.UsableRemainingKWh)),
            ("Pack voltage", Display.Volts(data.PackVoltage)),
            ("Cell voltage min / max", $"{Display.Volts(data.BrickVoltageMin, "0.000")} / {Display.Volts(data.BrickVoltageMax, "0.000")}"),
            ("Cell spread", Display.Millivolts(health.CellSpreadMv)),
            ("Pack temperature min / max", $"{Display.Celsius(data.TempMinC)} / {Display.Celsius(data.TempMaxC)}"),
        ]);

        if (chargeTest is not null)
            ChargeTestSection(html, chargeTest);

        var carInfo = CarInfo.WithEstimates(data);
        var configuration = CarInfo.Fields.Where(f => carInfo.ContainsKey(f.Key)).Select(f => (f.Label, carInfo[f.Key])).ToArray();
        if (configuration.Length > 0)
        {
            Section(html, "Configuration and infotainment", configuration);
            html.Append(CultureInfo.InvariantCulture, $"<p class=\"muted\">{Encode(CarInfo.NotBroadcastNote)}</p>");
        }

        Section(html, "Lifetime",
        [
            ("Energy charged", Display.KWh(data.ChargeTotalKWh)),
            ("Energy discharged", Display.KWh(data.DischargeTotalKWh)),
            ("Equivalent full cycles", Display.Number(health.EquivalentFullCycles)),
        ]);

        if (currentAlerts is not null)
        {
            html.Append("<h2>Current alerts</h2>");
            if (alertCoverage is { Supported: false })
                html.Append("<p class=\"muted\">This car's own alert messages are not decoded for its platform; only this tool's checks are shown.</p>");
            else if (alertCoverage is { } coverage)
                html.Append(CultureInfo.CurrentCulture, $"<p class=\"muted\">Alert messages received from {coverage.MessagesReceived} of {coverage.MessagesKnown} controllers.</p>");

            if (currentAlerts.Count == 0)
                html.Append("<p>No active alerts.</p>");
            foreach (var alert in currentAlerts)
                AlertBlock(html, alert.Definition, $"Active since {alert.Since.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}");
        }

        if (alertHistory is { Count: > 0 })
        {
            html.Append("<h2>Alert history (recorded by this tool)</h2>");
            foreach (var entry in alertHistory)
            {
                var when = entry.Occurrences == 1
                    ? $"Seen once, {entry.FirstSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}"
                    : $"Seen {entry.Occurrences} times, first {entry.FirstSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}, last {entry.LastSeen.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}";
                AlertBlock(html, entry.Definition, entry.ActiveNow ? when + " · active now" : when);
            }
        }

        if (health.Notes.Count > 0)
        {
            html.Append("<h2>Notes</h2><ul>");
            foreach (var note in health.Notes)
                html.Append(CultureInfo.InvariantCulture, $"<li>{Encode(note)}</li>");
            html.Append("</ul>");
        }

        html.Append("<p class=\"muted\">Values are the battery management system's own estimates, read passively from the vehicle CAN bus. "
                    + "This is not an official Tesla battery test.</p>");
        html.Append("</main></body></html>");
        return html.ToString();
    }

    /// <summary>
    /// A short plain-text summary for pasting into a message, listing or service ticket.
    /// </summary>
    public static string ToText(
        BatteryData data,
        HealthReport health,
        bool miles,
        IReadOnlyList<ActiveAlert>? currentAlerts = null,
        IReadOnlyList<CheckItem>? buyerCheck = null,
        ChargeTestResult? chargeTest = null)
    {
        var vin = data.Vin is { } v ? new VinInfo(v) : null;
        var lines = new List<string>
        {
            vin is null ? "Tesla battery health" : $"Tesla {ModelName(vin.Model)} {vin.ModelYear} battery health",
        };

        void Add(string label, string value)
        {
            if (value != Display.Missing)
                lines.Add($"{label}: {value}");
        }

        Add("VIN", data.Vin ?? Display.Missing);
        Add("Odometer", Display.Distance(data.OdometerKm, miles, "N0"));
        Add("State of health", Display.Percent(health.StateOfHealthPercent));
        Add("Degradation", Display.Percent(health.DegradationPercent));
        Add($"Original capacity ({OriginLabel(health)})", Display.KWh(health.OriginalKWh));
        Add("Current capacity", Display.KWh(health.CurrentKWh));
        Add("Usable at 100%", Display.KWh(health.UsableKWh));
        Add("Rated range at 100%", Display.Distance(health.FullRangeKm, miles));
        Add("Cell spread", Display.Millivolts(health.CellSpreadMv));
        Add("Equivalent full cycles", Display.Number(health.EquivalentFullCycles));
        if (data.CarInfo.TryGetValue("pack", out var pack))
            Add("Battery pack (configured)", pack);

        if (currentAlerts is not null)
        {
            lines.Add(currentAlerts.Count == 0
                ? "Active alerts: none"
                : "Active alerts: " + string.Join("; ", currentAlerts.Select(a => $"{a.Definition.Code} {a.Definition.Title}")));
        }

        if (buyerCheck is { Count: > 0 })
        {
            lines.Add($"Buyer check: {BuyerCheck.Summarize(buyerCheck)}");
            foreach (var item in buyerCheck.Where(i => i.Status is CheckStatus.Attention or CheckStatus.Fail))
                lines.Add($"  {BuyerCheck.Describe(item.Status)} - {item.Title}: {item.Summary}");
        }

        if (chargeTest is not null)
            lines.Add(chargeTest.ToText());

        lines.Add("Read from the car's battery management system over the diagnostic port; not an official Tesla test.");
        return string.Join(Environment.NewLine, lines);
    }

    /// <summary>Where the original capacity came from, e.g. "estimated from the pack type: 85 kWh (2012–2016)".</summary>
    public static string OriginLabel(HealthReport health) =>
        health.Pack is { } pack ? $"{Display.Source(health.OriginalSource)}: {pack.Name}" : Display.Source(health.OriginalSource);

    public static string ModelName(TeslaModel model) => model switch
    {
        TeslaModel.ModelS => "Model S",
        TeslaModel.ModelX => "Model X",
        TeslaModel.Model3 => "Model 3",
        TeslaModel.ModelY => "Model Y",
        TeslaModel.Cybertruck => "Cybertruck",
        _ => "Unknown",
    };

    private static void ChargeTestSection(StringBuilder html, ChargeTestResult test)
    {
        Section(html, "Charging test",
        [
            ("Result", test.Summary),
            ("Pack resistance (approx.)", ChargeTestResult.MilliOhm(test.PackResistanceMilliOhm, "0")),
            ("Average per cell group (approx.)", ChargeTestResult.MilliOhm(test.AverageGroupMilliOhm, "0.00")),
            ("Measurements", test.StepsText),
            ("Conditions", test.Conditions),
        ]);

        if (test.Flagged.Count > 0)
        {
            html.Append("<ul>");
            foreach (var group in test.Flagged.Take(10))
                html.Append(CultureInfo.InvariantCulture, $"<li>{Encode(ChargeTestResult.Describe(group))}</li>");
            html.Append("</ul>");
        }

        html.Append("<p class=\"muted\">Each cell group was compared at rest and while charging: a group whose voltage rises more than the others "
                    + "under the same current has higher internal resistance. One weak group can make the car limit power and charging. "
                    + "Cold packs read higher, so compare tests made at similar temperatures.</p>");
    }

    private static void AlertBlock(StringBuilder html, AlertDefinition alert, string when)
    {
        html.Append(CultureInfo.InvariantCulture, $"<div class=\"alert {alert.Severity}\"><b>{Encode(alert.Title)}</b><code>{Encode(alert.Code)}</code>");
        html.Append(CultureInfo.InvariantCulture, $"<div>{Encode(alert.Description)}</div>");
        if (alert.Interpreted && alert.InternalName is { } name)
            html.Append(CultureInfo.InvariantCulture, $"<div class=muted><i>Interpreted from Tesla's internal name {Encode(name)}.</i></div>");
        html.Append(CultureInfo.InvariantCulture, $"<div class=\"muted\">{Encode(alert.Severity.ToString())} · {Encode(alert.System)} · {Encode(when)}</div></div>");
    }

    private static void Section(StringBuilder html, string title, (string Label, string Value)[] rows)
    {
        html.Append(CultureInfo.InvariantCulture, $"<h2>{Encode(title)}</h2><table>");
        foreach (var (label, value) in rows)
            html.Append(CultureInfo.InvariantCulture, $"<tr><td>{Encode(label)}</td><td>{Encode(value)}</td></tr>");
        html.Append("</table>");
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text);
}

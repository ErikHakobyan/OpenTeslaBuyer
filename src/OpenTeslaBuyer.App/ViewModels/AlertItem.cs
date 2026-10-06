using System.Globalization;
using OpenTeslaBuyer.Core.Alerts;

namespace OpenTeslaBuyer.App.ViewModels;

/// <summary>One row in the alert lists.</summary>
/// <param name="Meta">Severity, system and timing in one line.</param>
/// <param name="Detail">Shown for descriptions interpreted from Tesla's internal name.</param>
public sealed record AlertItem(string Code, string Title, string Description, AlertSeverity Severity, string Meta, string? Detail)
{
    /// <summary>What the copy button puts on the clipboard, e.g. for searching the code or a service ticket.</summary>
    public string CopyText => $"{Code} {Title}: {Description} ({Meta})";

    public static AlertItem From(ActiveAlert alert) =>
        Create(alert.Definition, $"active since {Time(alert.Since)}");

    public static AlertItem From(AlertHistoryEntry entry)
    {
        var when = entry.Occurrences == 1
            ? $"seen once, {Time(entry.FirstSeen)}"
            : $"seen {entry.Occurrences} times, first {Time(entry.FirstSeen)}, last {Time(entry.LastSeen)}";
        return Create(entry.Definition, entry.ActiveNow ? when + ", active now" : when);
    }

    private static AlertItem Create(AlertDefinition definition, string when) => new(
        definition.Code,
        definition.Title,
        definition.Description,
        definition.Severity,
        $"{definition.Severity} · {definition.System} · {when}",
        definition.Interpreted && definition.InternalName is { } name ? $"Interpreted from Tesla's internal name {name}." : null);

    private static string Time(DateTimeOffset time)
    {
        var local = time.ToLocalTime();
        return local.Date == DateTime.Today
            ? local.ToString("T", CultureInfo.CurrentCulture)
            : local.ToString("g", CultureInfo.CurrentCulture);
    }
}

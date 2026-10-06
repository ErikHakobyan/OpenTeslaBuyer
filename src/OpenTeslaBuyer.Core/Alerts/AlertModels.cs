namespace OpenTeslaBuyer.Core.Alerts;

public enum AlertSeverity
{
    Info,
    Warning,
    Critical,
}

public enum AlertSource
{
    /// <summary>Broadcast by one of the car's controllers.</summary>
    Car,

    /// <summary>Raised by this tool from the decoded battery data.</summary>
    Tool,
}

/// <summary>What an alert code means.</summary>
/// <param name="Code">Tesla's code, e.g. <c>BMS_a064</c>, or this tool's own, e.g. <c>TOOL_w001</c>.</param>
/// <param name="System">The controller or area that raises it, in plain words.</param>
/// <param name="InternalName">Tesla's internal signal name, e.g. <c>SW_SOC_Imbalance</c>.</param>
/// <param name="Interpreted">True when the description was derived from the internal name rather than written by hand.</param>
public sealed record AlertDefinition(
    string Code,
    string System,
    string Title,
    string Description,
    AlertSeverity Severity,
    AlertSource Source,
    string? InternalName = null,
    bool Interpreted = false);

/// <summary>An alert that is active now.</summary>
public sealed record ActiveAlert(AlertDefinition Definition, DateTimeOffset Since);

/// <summary>One period during which an alert was active, as recorded by this tool.</summary>
public sealed class AlertEpisode
{
    public required string Code { get; init; }

    public required DateTimeOffset Start { get; init; }

    /// <summary>Null while the alert is still active (or if the tool stopped before recording the end).</summary>
    public DateTimeOffset? End { get; set; }

    /// <summary>The alert was still active when the connection ended, so the real end is later than <see cref="End"/>.</summary>
    public bool EndedWithSession { get; set; }
}

/// <summary>Everything recorded about one alert code for a car.</summary>
public sealed record AlertHistoryEntry(
    AlertDefinition Definition,
    int Occurrences,
    DateTimeOffset FirstSeen,
    DateTimeOffset LastSeen,
    bool ActiveNow);

/// <summary>How many of the car's alert messages this connection has received.</summary>
/// <param name="Supported">False when the platform's alert messages are not decoded (pre-2021 Model S/X).</param>
public sealed record AlertCoverage(bool Supported, int MessagesKnown, int MessagesReceived);

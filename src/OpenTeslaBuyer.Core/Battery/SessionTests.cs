using OpenTeslaBuyer.Core.Buyer;

namespace OpenTeslaBuyer.Core.Battery;

/// <summary>A connection's parked tests: shown on the Tests page, written into the report and kept with the saved check.</summary>
public sealed record SessionTests(
    ChargeTestResult? ChargeTest = null,
    DriftResult? Overnight = null,
    ChargerReport? Charger = null,
    TwelveVoltReport? TwelveVolt = null)
{
    public static SessionTests None { get; } = new();

    /// <summary>The charger report, if AC charging was measured or a fault seen.</summary>
    public ChargerReport? ChargerIfMeasured => Charger is { } c && (c.Measuring || c.Overall == CheckStatus.Fail) ? c : null;

    /// <summary>The 12 V report, if anything was judged.</summary>
    public TwelveVoltReport? TwelveVoltIfMeasured =>
        TwelveVolt is { } t && t.Findings.Any(f => f.Status != CheckStatus.NotAvailable) ? t : null;
}

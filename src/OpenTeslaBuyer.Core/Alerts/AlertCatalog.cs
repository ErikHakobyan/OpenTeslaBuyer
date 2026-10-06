using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTeslaBuyer.Core.Can;

namespace OpenTeslaBuyer.Core.Alerts;

/// <summary>
/// The car's alert-matrix messages and the meaning of every bit in them. Each controller broadcasts its active
/// alerts as one-bit flags, about once a second, in one or more 8-byte messages; larger matrices are split into
/// pages selected by the low four bits of the first byte.
/// </summary>
public sealed class AlertCatalog
{
    private readonly Dictionary<uint, AlertMatrixMessage> _byId;
    private readonly Dictionary<string, AlertDefinition> _byCode;

    private AlertCatalog(IReadOnlyList<AlertMatrixMessage> messages)
    {
        Messages = messages;
        _byId = messages.ToDictionary(m => m.Id);
        _byCode = messages.SelectMany(m => m.Alerts).ToDictionary(a => a.Definition.Code, a => a.Definition);
    }

    /// <summary>Model 3, Model Y and 2021+ Model S/X, generated from a community DBC by <c>tools/GenerateAlertCatalog.cs</c>.</summary>
    public static AlertCatalog Model3Platform { get; } = Load("alert-catalog-model3.json");

    /// <summary>Ordered most battery-relevant first.</summary>
    public IReadOnlyList<AlertMatrixMessage> Messages { get; }

    public bool TryGetMessage(uint id, out AlertMatrixMessage message) => _byId.TryGetValue(id, out message!);

    public AlertDefinition? Find(string code) => _byCode.GetValueOrDefault(code);

    private static AlertCatalog Load(string resource)
    {
        using var stream = typeof(AlertCatalog).Assembly.GetManifestResourceStream($"OpenTeslaBuyer.Core.Alerts.{resource}")
                           ?? throw new InvalidOperationException($"Embedded alert catalog {resource} is missing.");
        var file = JsonSerializer.Deserialize(stream, AlertJsonContext.Default.CatalogFile)
                   ?? throw new InvalidOperationException($"Alert catalog {resource} is empty.");

        var messages = file.Messages.Select(m =>
        {
            var ecu = m.Name[..m.Name.IndexOf('_')];
            var page = m.PageStart is { } start && m.PageLength is { } length ? new Signal(start, length) : null;
            var alerts = m.Alerts
                .Select(a => new AlertBit(AlertDescriptions.Describe(a.Code, ecu, a.Name), a.Page, new Signal(a.Bit, 1)))
                .ToList();
            return new AlertMatrixMessage((uint)m.Id, m.Name, ecu, page, alerts);
        });

        return new AlertCatalog(messages.OrderBy(m => AlertDescriptions.Priority(m.Ecu)).ThenBy(m => m.Id).ToList());
    }
}

/// <summary>One alert-matrix message.</summary>
/// <param name="Page">The page selector, or null for a single-page matrix.</param>
public sealed record AlertMatrixMessage(uint Id, string Name, string Ecu, Signal? Page, IReadOnlyList<AlertBit> Alerts)
{
    public int PageCount { get; } = Alerts.Select(a => a.Page ?? 0).DefaultIfEmpty(0).Max() + 1;

    /// <summary>The state of every alert carried by this frame's page.</summary>
    public IEnumerable<(string Code, bool Active)> Decode(byte[] data)
    {
        int? page = Page is null ? null : (int)Page.Raw(data);
        foreach (var alert in Alerts)
        {
            if (alert.Page == page && alert.Bit.Fits(data))
                yield return (alert.Definition.Code, alert.Bit.Raw(data) == 1);
        }
    }

    /// <summary>Builds one page with the given alerts set; used by the simulator and tests.</summary>
    public byte[] Encode(int page, IEnumerable<string> activeCodes)
    {
        var data = new byte[8];
        Page?.EncodeRaw(data, (ulong)page);
        var active = activeCodes.ToHashSet();
        foreach (var alert in Alerts.Where(a => (a.Page ?? 0) == page && active.Contains(a.Definition.Code)))
            alert.Bit.EncodeRaw(data, 1);
        return data;
    }
}

public sealed record AlertBit(AlertDefinition Definition, int? Page, Signal Bit);

internal sealed class CatalogFile
{
    public List<CatalogMessage> Messages { get; set; } = [];
}

internal sealed class CatalogMessage
{
    public int Id { get; set; }

    public string Name { get; set; } = "";

    public int? PageStart { get; set; }

    public int? PageLength { get; set; }

    public List<CatalogAlert> Alerts { get; set; } = [];
}

internal sealed class CatalogAlert
{
    public string Code { get; set; } = "";

    public string Name { get; set; } = "";

    public int? Page { get; set; }

    public int Bit { get; set; }
}

/// <summary>Source-generated serialization, so the library works under trimming/AOT (e.g. a future mobile app).</summary>
[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase, WriteIndented = true)]
[JsonSerializable(typeof(CatalogFile))]
[JsonSerializable(typeof(List<AlertEpisode>))]
internal sealed partial class AlertJsonContext : JsonSerializerContext;

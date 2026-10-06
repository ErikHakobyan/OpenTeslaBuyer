using System.Text.Json;
using OpenTeslaBuyer.Core.Battery;

namespace OpenTeslaBuyer.Core.Alerts;

/// <summary>
/// Turns the car's alert flags and this tool's checks into current alerts and a history of episodes
/// (when each alert started and ended). One tracker per connection; history is kept per VIN in an
/// <see cref="AlertHistoryStore"/> so it builds up across sessions.
/// </summary>
/// <remarks>
/// "History" means what this tool has observed while connected. The car's own stored alert log is only
/// reachable through Tesla's authenticated diagnostics, which this tool does not use.
/// </remarks>
public sealed class AlertTracker(IAlertHistoryStore? store = null)
{
    /// <summary>How long a state must hold before an alert counts as started or ended; filters flicker around thresholds.</summary>
    public static readonly TimeSpan Debounce = TimeSpan.FromSeconds(3);

    private readonly Dictionary<string, DateTimeOffset> _risingSince = [];
    private readonly Dictionary<string, DateTimeOffset> _fallingSince = [];
    private readonly Dictionary<string, AlertEpisode> _open = [];
    private readonly List<AlertEpisode> _session = [];
    private List<AlertEpisode> _earlier = [];

    public string? Vin { get; private set; }

    public IReadOnlyList<ActiveAlert> Current { get; private set; } = [];

    public AlertCoverage Coverage { get; private set; } = new(false, 0, 0);

    /// <summary>Changes whenever <see cref="Current"/> or the history changes, so a UI can skip redundant redraws.</summary>
    public int Version { get; private set; }

    public void Update(BatteryData data, HealthReport health, DateTimeOffset now)
    {
        var changed = false;
        if (Vin is null && data.Vin is { } vin)
        {
            Vin = vin;
            _earlier = store?.Load(vin) ?? [];
            changed = true;
        }

        var active = data.CarAlerts.Where(a => a.Value).Select(a => a.Key)
            .Concat(ToolDiagnostics.Evaluate(data, health).Where(t => t.Active).Select(t => t.Code))
            .ToHashSet();

        foreach (var code in active)
        {
            _fallingSince.Remove(code);
            if (_open.ContainsKey(code))
                continue;

            var since = _risingSince.TryAdd(code, now) ? now : _risingSince[code];
            if (now - since < Debounce)
                continue;

            var episode = new AlertEpisode { Code = code, Start = since };
            _open[code] = episode;
            _session.Add(episode);
            _risingSince.Remove(code);
            changed = true;
        }

        foreach (var code in _risingSince.Keys.Where(c => !active.Contains(c)).ToList())
            _risingSince.Remove(code); // did not last long enough to count

        foreach (var code in _open.Keys.Where(c => !active.Contains(c)).ToList())
        {
            var since = _fallingSince.TryAdd(code, now) ? now : _fallingSince[code];
            if (now - since < Debounce)
                continue;

            _open[code].End = since;
            _open.Remove(code);
            _fallingSince.Remove(code);
            changed = true;
        }

        var coverage = CoverageOf(data);
        if (coverage != Coverage)
        {
            Coverage = coverage;
            changed = true;
        }

        if (changed)
            Publish();
    }

    /// <summary>Closes alerts still active when the connection ends and saves the history.</summary>
    public void EndSession(DateTimeOffset now)
    {
        foreach (var episode in _open.Values)
        {
            episode.End = now;
            episode.EndedWithSession = true;
        }

        _open.Clear();
        _risingSince.Clear();
        _fallingSince.Clear();
        Publish();
    }

    /// <summary>One entry per alert code ever recorded for this car (or this session, before the VIN is known), most recent first.</summary>
    public IReadOnlyList<AlertHistoryEntry> History() => Summarize(_earlier.Concat(_session), _open.Keys.ToHashSet());

    /// <summary>Groups episodes into one entry per alert code, active ones first, then most recently seen.</summary>
    public static IReadOnlyList<AlertHistoryEntry> Summarize(IEnumerable<AlertEpisode> episodes, IReadOnlySet<string>? activeCodes = null) =>
        episodes
            .GroupBy(e => e.Code)
            .Select(g => new AlertHistoryEntry(
                AlertDefinitions.Find(g.Key),
                g.Count(),
                g.Min(e => e.Start),
                g.Max(e => e.End ?? e.Start),
                activeCodes?.Contains(g.Key) == true))
            .OrderByDescending(h => h.ActiveNow)
            .ThenByDescending(h => h.LastSeen)
            .ToList();

    private void Publish()
    {
        Current = _open.Values
            .Select(e => new ActiveAlert(AlertDefinitions.Find(e.Code), e.Start))
            .OrderByDescending(a => a.Definition.Severity)
            .ThenBy(a => a.Definition.Code, StringComparer.Ordinal)
            .ToList();
        Version++;
        if (Vin is not null)
            store?.Save(Vin, _earlier.Concat(_session));
    }

    private static AlertCoverage CoverageOf(BatteryData data)
    {
        if (data.Platform != VehiclePlatform.Model3Family)
            return new AlertCoverage(false, 0, 0);

        var messages = AlertCatalog.Model3Platform.Messages;
        return new AlertCoverage(true, messages.Count, messages.Count(m => data.SeenIds.Contains(m.Id)));
    }
}

/// <summary>Where alert episodes are kept between sessions, per VIN.</summary>
public interface IAlertHistoryStore
{
    List<AlertEpisode> Load(string vin);

    void Save(string vin, IEnumerable<AlertEpisode> episodes);
}

/// <summary>Keeps alert episodes per VIN as JSON files in a folder (used by earlier versions; now imported into the database).</summary>
public sealed class AlertHistoryStore(string directory) : IAlertHistoryStore
{
    /// <summary>Oldest episodes beyond this are dropped so the files stay small.</summary>
    public const int MaxEpisodesPerVin = 5000;

    public List<AlertEpisode> Load(string vin)
    {
        try
        {
            using var stream = File.OpenRead(PathFor(vin));
            return JsonSerializer.Deserialize(stream, AlertJsonContext.Default.ListAlertEpisode) ?? [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return [];
        }
    }

    public void Save(string vin, IEnumerable<AlertEpisode> episodes)
    {
        var kept = episodes.OrderBy(e => e.Start).TakeLast(MaxEpisodesPerVin).ToList();
        try
        {
            Directory.CreateDirectory(directory);
            var temporary = PathFor(vin) + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(kept, AlertJsonContext.Default.ListAlertEpisode));
            File.Move(temporary, PathFor(vin), overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // History is a convenience; a locked or read-only folder must not break a session.
        }
    }

    private string PathFor(string vin) =>
        Path.Combine(directory, string.Concat(vin.Where(char.IsAsciiLetterOrDigit)) + ".alerts.json");
}

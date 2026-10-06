using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using OpenTeslaBuyer.App.ViewModels;
using OpenTeslaBuyer.Core.Battery;

namespace OpenTeslaBuyer.App;

/// <summary>The app's colours: follow Windows, or always light or dark.</summary>
public enum AppTheme
{
    System,
    Light,
    Dark,
}

/// <summary>Connection preferences remembered between runs, in %LOCALAPPDATA%\OpenTeslaBuyer\settings.json.</summary>
internal sealed class AppSettings
{
    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true, Converters = { new JsonStringEnumConverter() } };

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTeslaBuyer", "settings.json");

    public SourceKind Source { get; set; } = SourceKind.Elm;

    public string? Port { get; set; }

    public int Baud { get; set; } = 115200;

    public string? ReplayFile { get; set; }

    public bool UseMiles { get; set; }

    public VehiclePlatform Platform { get; set; }

    public EnergyLayout Layout { get; set; }

    /// <summary>Earlier versions kept these per-VIN choices here; they are imported into the database once.</summary>
    public Dictionary<string, double> OriginalCapacityByVin { get; set; } = [];

    /// <inheritdoc cref="OriginalCapacityByVin"/>
    public Dictionary<string, string> PackByVin { get; set; } = [];

    public bool ImportedLegacyData { get; set; }

    /// <summary>Folder holding the database; null means the default under %LOCALAPPDATA%.</summary>
    public string? DataFolder { get; set; }

    public string? RecordingsFolder { get; set; }

    public string? ReportsFolder { get; set; }

    /// <summary>Save a check when a simulator session ends (off by default, to keep the history to real cars).</summary>
    public bool SaveSimulatorChecks { get; set; }

    /// <summary>Save a check when a replay ends (off by default: replaying the same log would add duplicates).</summary>
    public bool SaveReplayChecks { get; set; }

    /// <summary>Start recording automatically whenever a real adapter connects.</summary>
    public bool AutoRecord { get; set; }

    /// <summary>Light, dark, or the Windows setting (the default).</summary>
    public AppTheme Theme { get; set; }

    public static AppSettings Load()
    {
        try
        {
            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath), Options) ?? new AppSettings();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Preferences are a convenience; never fail on them.
        }
    }
}

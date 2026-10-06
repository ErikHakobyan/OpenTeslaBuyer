using System.IO;
using OpenTeslaBuyer.Core.Storage;

namespace OpenTeslaBuyer.App;

/// <summary>
/// What every page shares: preferences, the database and the folders for recordings and reports.
/// Preferences stay in %LOCALAPPDATA%\OpenTeslaBuyer\settings.json; the database lives in the data folder,
/// which the user can move (e.g. to OneDrive or a USB drive).
/// </summary>
public sealed class AppServices : IDisposable
{
    /// <summary>The app's name until October 2026; its folders are still read.</summary>
    internal const string OldName = "TeslaBatteryHealth";

    public AppServices()
    {
        CopyDataFromOldName();
        Settings = AppSettings.Load();
        Database = OpenDatabase(DataFolder);
        ImportLegacyData();
    }

    public static string DefaultDataFolder { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenTeslaBuyer");

    /// <summary>Documents\OpenTeslaBuyer, or the old name's folder if that is the one that exists (its recordings are listed by path).</summary>
    public static string DefaultFilesFolder { get; } = PreferExisting(Environment.SpecialFolder.MyDocuments);

    internal AppSettings Settings { get; }

    public AppDatabase Database { get; private set; }

    public string DataFolder => Settings.DataFolder ?? DefaultDataFolder;

    public string RecordingsFolder => Settings.RecordingsFolder ?? Path.Combine(DefaultFilesFolder, "Recordings");

    public string ReportsFolder => Settings.ReportsFolder ?? Path.Combine(DefaultFilesFolder, "Reports");

    /// <summary>Raised after checks, cars or recordings change, so open pages can reload.</summary>
    public event EventHandler? DataChanged;

    public void NotifyDataChanged() => DataChanged?.Invoke(this, EventArgs.Empty);

    /// <summary>
    /// Uses <paramref name="folder"/> for the database: copies the current one there, or switches to the one already
    /// there (so a shared folder such as OneDrive can be used from several PCs).
    /// </summary>
    public void MoveDataFolder(string folder)
    {
        var target = Path.Combine(folder, AppDatabase.FileName);
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(folder);
            Database.BackupTo(target);
        }

        var opened = OpenDatabase(folder);
        Database.Dispose();
        Database = opened;
        Settings.DataFolder = folder;
        Settings.Save();
        NotifyDataChanged();
    }

    public void SetRecordingsFolder(string folder)
    {
        Settings.RecordingsFolder = folder;
        Settings.Save();
        NotifyDataChanged();
    }

    public void SetReportsFolder(string folder)
    {
        Settings.ReportsFolder = folder;
        Settings.Save();
    }

    public void Dispose() => Database.Dispose();

    private static AppDatabase OpenDatabase(string folder) => new(Path.Combine(folder, AppDatabase.FileName));

    private static string PreferExisting(Environment.SpecialFolder root)
    {
        var current = Path.Combine(Environment.GetFolderPath(root), "OpenTeslaBuyer");
        var old = Path.Combine(Environment.GetFolderPath(root), OldName);
        return !Directory.Exists(current) && Directory.Exists(old) ? old : current;
    }

    /// <summary>
    /// On the first start under the new name, copies the old name's settings, database and alert history across.
    /// The old folder is left as it was.
    /// </summary>
    private static void CopyDataFromOldName()
    {
        var old = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), OldName);
        if (Directory.Exists(DefaultDataFolder) || !Directory.Exists(old))
            return;

        try
        {
            CopyDirectory(old, DefaultDataFolder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Starting fresh is better than not starting; the old folder is untouched.
        }
    }

    private static void CopyDirectory(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var file in Directory.GetFiles(from))
            File.Copy(file, Path.Combine(to, Path.GetFileName(file)));
        foreach (var folder in Directory.GetDirectories(from))
            CopyDirectory(folder, Path.Combine(to, Path.GetFileName(folder)));
    }

    /// <summary>Earlier versions kept alert history in JSON files and per-VIN choices in settings; move them in once.</summary>
    private void ImportLegacyData()
    {
        if (Settings.ImportedLegacyData)
            return;

        LegacyImport.ImportAlertHistory(Database, Path.Combine(DefaultDataFolder, "AlertHistory"));
        LegacyImport.ImportCarChoices(Database, Settings.OriginalCapacityByVin, Settings.PackByVin);
        Settings.OriginalCapacityByVin.Clear();
        Settings.PackByVin.Clear();
        Settings.ImportedLegacyData = true;
        Settings.Save();
    }
}

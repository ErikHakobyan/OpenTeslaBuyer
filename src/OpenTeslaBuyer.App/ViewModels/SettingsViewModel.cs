using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenTeslaBuyer.Core.Storage;

namespace OpenTeslaBuyer.App.ViewModels;

/// <summary>A choice in the theme picker.</summary>
public sealed record ThemeOption(AppTheme Theme, string Title);

/// <summary>Where data is kept, what is saved automatically, appearance, units, and backups.</summary>
public sealed partial class SettingsViewModel(AppServices services, MainViewModel diagnostics) : ObservableObject, IPage
{
    [ObservableProperty]
    private string _dataStats = "";

    [ObservableProperty]
    private string? _feedback;

    public MainViewModel Diagnostics { get; } = diagnostics;

    public string DataFolder => services.DataFolder;

    public string RecordingsFolder => services.RecordingsFolder;

    public string ReportsFolder => services.ReportsFolder;

    public bool SaveSimulatorChecks
    {
        get => services.Settings.SaveSimulatorChecks;
        set => Update(() => services.Settings.SaveSimulatorChecks = value);
    }

    public bool SaveReplayChecks
    {
        get => services.Settings.SaveReplayChecks;
        set => Update(() => services.Settings.SaveReplayChecks = value);
    }

    public bool AutoRecord
    {
        get => services.Settings.AutoRecord;
        set => Update(() => services.Settings.AutoRecord = value);
    }

    public IReadOnlyList<ThemeOption> Themes { get; } =
    [
        new(AppTheme.System, "Match Windows"),
        new(AppTheme.Light, "Light"),
        new(AppTheme.Dark, "Dark"),
    ];

    public ThemeOption SelectedTheme
    {
        get => Themes.First(t => t.Theme == services.Settings.Theme);
        set
        {
            if (value is null || value.Theme == services.Settings.Theme)
                return;

            Update(() => services.Settings.Theme = value.Theme);
            Ui.ApplyTheme(value.Theme);
        }
    }

    public string Version { get; } = typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    public void Activate()
    {
        var (cars, checks, recordings) = services.Database.Counts();
        var size = File.Exists(services.Database.Path) ? Ui.Size(new FileInfo(services.Database.Path).Length) : "";
        DataStats = $"{cars} car(s), {checks} saved check(s), {recordings} recording(s) · database {size}";
        Feedback = null;
        OnPropertyChanged(string.Empty);
    }

    [RelayCommand]
    private void ChangeDataFolder()
    {
        if (PickFolder("Folder for the database", DataFolder) is not { } folder || string.Equals(folder, DataFolder, StringComparison.OrdinalIgnoreCase))
            return;

        var existing = File.Exists(Path.Combine(folder, AppDatabase.FileName));
        try
        {
            services.MoveDataFolder(folder);
            Feedback = existing
                ? "Switched to the database already in that folder. The previous one is unchanged."
                : "Database copied to the new folder and in use. The previous copy is unchanged.";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Feedback = $"Could not use that folder: {ex.Message}";
        }

        Activate();
    }

    [RelayCommand]
    private void ChangeRecordingsFolder()
    {
        if (PickFolder("Folder for recordings", RecordingsFolder) is { } folder)
        {
            services.SetRecordingsFolder(folder);
            Feedback = "New recordings will be saved there. Existing ones stay where they are and stay listed.";
            Activate();
        }
    }

    [RelayCommand]
    private void ChangeReportsFolder()
    {
        if (PickFolder("Folder for reports", ReportsFolder) is { } folder)
        {
            services.SetReportsFolder(folder);
            Activate();
        }
    }

    [RelayCommand]
    private void Open(string which) => Ui.OpenFolder(which switch
    {
        "data" => DataFolder,
        "recordings" => RecordingsFolder,
        _ => ReportsFolder,
    });

    [RelayCommand]
    private void Backup()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Back up the database",
            Filter = "Database (*.db)|*.db",
            FileName = $"openteslabuyer-{DateTime.Now:yyyyMMdd}.db",
        };
        if (dialog.ShowDialog() != true)
            return;

        try
        {
            if (File.Exists(dialog.FileName))
                File.Delete(dialog.FileName);
            services.Database.BackupTo(dialog.FileName);
            Feedback = $"Backup saved: {dialog.FileName}";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Microsoft.Data.Sqlite.SqliteException)
        {
            Feedback = $"Backup failed: {ex.Message}";
        }
    }

    private void Update(Action change)
    {
        change();
        services.Settings.Save();
        OnPropertyChanged(string.Empty);
    }

    private static string? PickFolder(string title, string current)
    {
        var dialog = new OpenFolderDialog { Title = title, InitialDirectory = Directory.Exists(current) ? current : null };
        return dialog.ShowDialog() == true ? dialog.FolderName : null;
    }
}

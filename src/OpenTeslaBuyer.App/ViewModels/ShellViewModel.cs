using CommunityToolkit.Mvvm.ComponentModel;

namespace OpenTeslaBuyer.App.ViewModels;

/// <summary>A page that reloads its data when it is shown.</summary>
public interface IPage
{
    void Activate();
}

/// <summary>An entry in the left menu.</summary>
/// <param name="Glyph">A Segoe Fluent Icons character.</param>
public sealed record NavItem(string Key, string Title, string Glyph);

/// <summary>The window: the left menu and the page it shows. Diagnostics is the original main screen.</summary>
public sealed partial class ShellViewModel : ObservableObject
{
    public const string DiagnosticsKey = "diagnostics";
    public const string ChargingKey = "charging";
    public const string RecordingsKey = "recordings";
    public const string ReportsKey = "reports";
    public const string HistoryKey = "history";
    public const string SettingsKey = "settings";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsDiagnostics), nameof(IsCharging), nameof(IsRecordings), nameof(IsReports), nameof(IsHistory), nameof(IsSettings))]
    private NavItem _selectedPage;

    public ShellViewModel()
    {
        Services = new AppServices();
        Diagnostics = new MainViewModel(Services);
        Charging = new ChargingViewModel(this);
        Recordings = new RecordingsViewModel(Services, this);
        Reports = new ReportsViewModel(Services);
        History = new CarHistoryViewModel(Services, this);
        Settings = new SettingsViewModel(Services, Diagnostics);
        Pages =
        [
            new(DiagnosticsKey, "Diagnostics", ""),
            new(ChargingKey, "Charging test", ""),
            new(RecordingsKey, "Recordings", ""),
            new(ReportsKey, "Reports", ""),
            new(HistoryKey, "Car history", ""),
            new(SettingsKey, "Settings", ""),
        ];
        _selectedPage = Pages[0];
        Services.DataChanged += (_, _) => ActivePage()?.Activate();
    }

    public AppServices Services { get; }

    public IReadOnlyList<NavItem> Pages { get; }

    public MainViewModel Diagnostics { get; }

    public ChargingViewModel Charging { get; }

    public RecordingsViewModel Recordings { get; }

    public ReportsViewModel Reports { get; }

    public CarHistoryViewModel History { get; }

    public SettingsViewModel Settings { get; }

    public bool IsDiagnostics => SelectedPage.Key == DiagnosticsKey;

    public bool IsCharging => SelectedPage.Key == ChargingKey;

    public bool IsRecordings => SelectedPage.Key == RecordingsKey;

    public bool IsReports => SelectedPage.Key == ReportsKey;

    public bool IsHistory => SelectedPage.Key == HistoryKey;

    public bool IsSettings => SelectedPage.Key == SettingsKey;

    /// <summary>Switches to Diagnostics and replays a recording there.</summary>
    public void ReplayRecording(string path)
    {
        SelectedPage = Pages[0];
        Diagnostics.StartReplay(path);
    }

    public void Shutdown()
    {
        Diagnostics.Shutdown();
        Services.Dispose();
    }

    partial void OnSelectedPageChanged(NavItem value) => ActivePage()?.Activate();

    private IPage? ActivePage() => SelectedPage.Key switch
    {
        ChargingKey => Charging,
        RecordingsKey => Recordings,
        ReportsKey => Reports,
        HistoryKey => History,
        SettingsKey => Settings,
        _ => null,
    };
}

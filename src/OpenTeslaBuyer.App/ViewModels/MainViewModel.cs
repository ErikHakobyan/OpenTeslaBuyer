using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.IO.Ports;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System.Windows;
using Microsoft.Win32;
using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Buyer;
using OpenTeslaBuyer.Core.Storage;
using OpenTeslaBuyer.Core.Can;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.App.ViewModels;

public enum SourceKind
{
    Elm,
    Slcan,
    Replay,
    SimulatorModel3,
    SimulatorModelS,
    SimulatorModel3Charging,
    SimulatorModelSCharging,
}

public sealed record SourceOption(SourceKind Kind, string Title);

public sealed record PlatformOption(VehiclePlatform Platform, string Title);

/// <summary>A choice in the pre-2021 Model S/X battery pack picker.</summary>
/// <param name="Key">"auto", "custom", or a <see cref="PackType.Key"/>.</param>
public sealed record PackOption(string Key, string Title)
{
    public const string Auto = "auto";
    public const string Custom = "custom";

    public bool IsPack => Key is not (Auto or Custom);
}

public sealed record LayoutOption(EnergyLayout Layout, string Title);

public sealed partial class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 500;

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly DispatcherTimer _copyFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly ConcurrentQueue<string> _pendingLog = new();
    private readonly AppSettings _settings;

    private readonly Metric _degradation = new("Degradation", "Capacity lost since new, as a share of the original capacity.");
    private readonly Metric _original = new("Original capacity");
    private readonly Metric _current = new("Current capacity", "The BMS's estimate of the full pack today, buffer included.");
    private readonly Metric _lost = new("Capacity lost");
    private readonly Metric _usable = new("Usable at 100%", "Current capacity minus the buffer the car keeps below 0%.");
    private readonly Metric _range = new("Rated range at 100%", "Usable energy divided by the car's rated consumption.");

    private readonly Metric _soc = new("State of charge");
    private readonly Metric _usableLeft = new("Usable energy left");
    private readonly Metric _buffer = new("Buffer", "Energy held back below 0% displayed charge.");
    private readonly Metric _toLimit = new("To charge limit");
    private readonly Metric _voltage = new("Pack voltage");
    private readonly Metric _currentAmps = new("Pack current");
    private readonly Metric _power = new("Pack power");
    private readonly Metric _temperature = new("Pack temperature");
    private readonly Metric _spread = new("Cell spread", "Difference between the highest and lowest cell group. Most meaningful with the car parked.");
    private readonly Metric _charged = new("Energy charged (lifetime)");
    private readonly Metric _discharged = new("Energy discharged (lifetime)");
    private readonly Metric _cycles = new("Equivalent full cycles", "Lifetime discharged energy divided by the original capacity.");

    private CancellationTokenSource? _cts;
    private BatteryMonitor? _monitor;
    private volatile CandumpWriter? _recorder;
    private BatteryData _data = new();
    private HealthReport _health = HealthCalculator.Evaluate(new BatteryData());
    private long _rateFrames;
    private DateTime _rateSampledAt = DateTime.UtcNow;
    private double _framesPerSecond;
    private string? _capacityVin;
    private bool _refreshing;
    private AlertTracker? _alerts;
    private int _alertsVersion = -1;
    private DateTimeOffset _connectedAt;
    private bool _autoRecording;
    private string _carInfoShown = "";
    private ChargeTestStatus? _chargeTest;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsSerialSource), nameof(IsElmSource), nameof(IsReplaySource))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private SourceOption _selectedSource;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string? _selectedPort;

    [ObservableProperty]
    private int _selectedBaud;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand))]
    private string? _replayFile;

    [ObservableProperty]
    private PlatformOption _selectedPlatform;

    [ObservableProperty]
    private LayoutOption _selectedLayout;

    [ObservableProperty]
    private bool _showCapacityPrompt;

    [ObservableProperty]
    private bool _showPackPicker;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsCustomPack))]
    private PackOption _selectedPack;

    [ObservableProperty]
    private string _packExplanation = "";

    [ObservableProperty]
    private string _capacityPromptText = "";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsIdle))]
    [NotifyCanExecuteChangedFor(nameof(ConnectCommand), nameof(DisconnectCommand))]
    private bool _isConnected;

    [ObservableProperty]
    private bool _isRecording;

    [ObservableProperty]
    private bool _useMiles;

    [ObservableProperty]
    private string _originalCapacityText = "";

    [ObservableProperty]
    private string? _originalCapacityError;

    [ObservableProperty]
    private double _sohValue;

    [ObservableProperty]
    private string _sohText = Display.Missing;

    [ObservableProperty]
    private HealthRating _rating;

    [ObservableProperty]
    private string _ratingText = Display.Rating(HealthRating.Unknown);

    [ObservableProperty]
    private string _vinText = Display.Missing;

    [ObservableProperty]
    private string? _vinWarning;

    [ObservableProperty]
    private string _modelText = Display.Missing;

    [ObservableProperty]
    private string _yearText = Display.Missing;

    [ObservableProperty]
    private string _plantText = Display.Missing;

    [ObservableProperty]
    private string _odometerText = Display.Missing;

    [ObservableProperty]
    private string _platformText = Display.Missing;

    [ObservableProperty]
    private string _layoutText = Display.Missing;

    [ObservableProperty]
    private bool _hasBricks;

    [ObservableProperty]
    private string _brickSummary = "No per-cell data yet.";

    [ObservableProperty]
    private string _statusText = "Not connected";

    [ObservableProperty]
    private string _frameText = "";

    [ObservableProperty]
    private string? _copyFeedback;

    [ObservableProperty]
    private bool _hasCarInfo;

    [ObservableProperty]
    private string _alertsHeader = "Alerts";

    [ObservableProperty]
    private string _buyerSummary = "Connect to run the buyer check.";

    [ObservableProperty]
    private string _alertSummary = "Connect to see the car's alerts.";

    [ObservableProperty]
    private bool _hasCurrentAlerts;

    [ObservableProperty]
    private bool _hasAlertHistory;

    public MainViewModel(AppServices services)
    {
        _services = services;
        Sources =
        [
            new(SourceKind.Elm, "OBDLink / ELM327"),
            new(SourceKind.Slcan, "CANable (SLCAN)"),
            new(SourceKind.Replay, "Replay a recording"),
            new(SourceKind.SimulatorModel3, "Simulator: Model 3"),
            new(SourceKind.SimulatorModelS, "Simulator: Model S"),
            new(SourceKind.SimulatorModel3Charging, "Simulator: Model 3 charging"),
            new(SourceKind.SimulatorModelSCharging, "Simulator: Model S charging"),
        ];
        Platforms =
        [
            new(VehiclePlatform.Auto, VehicleProfiles.Describe(VehiclePlatform.Auto)),
            new(VehiclePlatform.Model3Family, VehicleProfiles.Describe(VehiclePlatform.Model3Family)),
            new(VehiclePlatform.LegacyModelSX, VehicleProfiles.Describe(VehiclePlatform.LegacyModelSX)),
        ];
        Layouts =
        [
            new(EnergyLayout.Auto, "Detect automatically"),
            new(EnergyLayout.Multiplexed, "Multiplexed (newer Model 3/Y firmware)"),
            new(EnergyLayout.Bits11, "11-bit"),
            new(EnergyLayout.Bits10, "10-bit (early firmware)"),
        ];
        PackOptions =
        [
            new(PackOption.Auto, "Estimate automatically"),
            .. PackEstimator.Packs.Select(pack => new PackOption(pack.Key, pack.Name)),
            new(PackOption.Custom, "Enter the capacity"),
        ];
        _selectedPack = PackOptions[0];
        HealthMetrics = [_degradation, _original, _current, _lost, _usable, _range];
        LiveMetrics = [_soc, _usableLeft, _buffer, _toLimit, _voltage, _currentAmps, _power, _temperature, _spread, _charged, _discharged, _cycles];

        _settings = services.Settings;
        _selectedSource = Sources.FirstOrDefault(s => s.Kind == _settings.Source) ?? Sources[0];
        _selectedBaud = BaudRates.Contains(_settings.Baud) ? _settings.Baud : 115200;
        _replayFile = _settings.ReplayFile;
        _useMiles = _settings.UseMiles;
        _selectedPlatform = Platforms.FirstOrDefault(p => p.Platform == _settings.Platform) ?? Platforms[0];
        _selectedLayout = Layouts.FirstOrDefault(l => l.Layout == _settings.Layout) ?? Layouts[0];
        RefreshPorts();
        if (_settings.Port is { } port && Ports.Contains(port))
            SelectedPort = port;

        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Refresh();
        _copyFeedbackTimer.Tick += (_, _) =>
        {
            _copyFeedbackTimer.Stop();
            CopyFeedback = null;
        };
        Refresh();
    }

    public IReadOnlyList<SourceOption> Sources { get; }

    public IReadOnlyList<PlatformOption> Platforms { get; }

    public IReadOnlyList<PackOption> PackOptions { get; }

    public bool IsCustomPack => SelectedPack.Key == PackOption.Custom;

    public IReadOnlyList<LayoutOption> Layouts { get; }

    /// <summary>115200 suits OBDLink USB and vLinker; many cheap ELM327 USB clones use 38400. Bluetooth ports ignore it.</summary>
    public IReadOnlyList<int> BaudRates { get; } = [38400, 115200, 230400, 500000];

    public ObservableCollection<string> Ports { get; } = [];

    public ObservableCollection<Metric> HealthMetrics { get; }

    public ObservableCollection<Metric> LiveMetrics { get; }

    public ObservableCollection<BrickBar> Bricks { get; } = [];

    public ObservableCollection<string> Notes { get; } = [];

    public ObservableCollection<AlertItem> CurrentAlerts { get; } = [];

    public ObservableCollection<AlertItem> AlertHistory { get; } = [];

    public ObservableCollection<CheckItem> BuyerItems { get; } = [];

    public ObservableCollection<Metric> InfotainmentInfo { get; } = [];

    public ObservableCollection<Metric> ConfigurationInfo { get; } = [];

    public string NotBroadcastNote => CarInfo.NotBroadcastNote;

    public ObservableCollection<string> Log { get; } = [];

    public bool IsIdle => !IsConnected;

    public bool IsSerialSource => SelectedSource.Kind is SourceKind.Elm or SourceKind.Slcan;

    public bool IsElmSource => SelectedSource.Kind == SourceKind.Elm;

    public bool IsReplaySource => SelectedSource.Kind == SourceKind.Replay;

    /// <summary>The latest decoded data, refreshed four times a second while connected.</summary>
    public BatteryData LatestData => _data;

    /// <summary>The charging test of the current (or last) connection.</summary>
    public ChargeTestStatus? ChargeTest => _chargeTest;

    /// <summary>Raised after every refresh, so other pages can follow the connection.</summary>
    public event EventHandler? Refreshed;

    /// <summary>Discards the charging test's measurements and starts it again on the same connection.</summary>
    public void RestartChargeTest()
    {
        _monitor?.RestartChargeTest();
        Refresh();
    }

    public void Shutdown()
    {
        _cts?.Cancel();
        IsRecording = false;
        _settings.Source = SelectedSource.Kind;
        _settings.Port = SelectedPort;
        _settings.Baud = SelectedBaud;
        _settings.ReplayFile = ReplayFile;
        _settings.UseMiles = UseMiles;
        _settings.Platform = SelectedPlatform.Platform;
        _settings.Layout = SelectedLayout.Layout;
        _settings.Save();
    }

    private string RecordingsFolder => _services.RecordingsFolder;

    private bool CanConnect() => !IsConnected && SelectedSource.Kind switch
    {
        SourceKind.Elm or SourceKind.Slcan => !string.IsNullOrEmpty(SelectedPort),
        SourceKind.Replay => File.Exists(ReplayFile),
        _ => true,
    };

    [RelayCommand(CanExecute = nameof(CanConnect))]
    private async Task ConnectAsync()
    {
        var adapter = CreateAdapter();
        var profile = VehicleProfiles.Create(SelectedPlatform.Platform, SelectedLayout.Layout);
        var monitor = new BatteryMonitor(profile);
        _alerts = new AlertTracker(new DatabaseAlertHistoryStore(_services.Database));
        _connectedAt = DateTimeOffset.Now;
        _alertsVersion = -1;
        using var cts = new CancellationTokenSource();
        _cts = cts;
        _monitor = monitor;
        _rateFrames = 0;
        _rateSampledAt = DateTime.UtcNow;

        // A capacity typed for the previous car must not carry over; this car's is loaded once its VIN is read.
        _capacityVin = null;
        OriginalCapacityText = "";
        SelectedPack = PackOptions[0];
        IsConnected = true;
        StatusText = $"Connected: {SelectedSource.Title}";
        _timer.Start();
        if (_settings.AutoRecord && SelectedSource.Kind is SourceKind.Elm or SourceKind.Slcan && !IsRecording)
        {
            _autoRecording = true;
            IsRecording = true;
        }

        try
        {
            await Task.Run(
                async () =>
                {
                    await foreach (var frame in adapter.StreamAsync(() => profile.MonitoredIds, EnqueueLog, cts.Token))
                    {
                        monitor.Process(frame);
                        _recorder?.Write(frame);
                    }
                },
                cts.Token);
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            EnqueueLog("Disconnected.");
        }
        catch (UnauthorizedAccessException)
        {
            EnqueueLog($"{SelectedPort} is in use by another program. Close other OBD apps and try again.");
        }
        catch (Exception ex)
        {
            EnqueueLog($"Error: {ex.Message}");
        }
        finally
        {
            _cts = null;
            IsConnected = false;
            _timer.Stop();
            StatusText = "Not connected";
            if (_autoRecording)
            {
                _autoRecording = false;
                IsRecording = false;
            }

            Refresh();
            SaveCheck();
            _alerts.EndSession(DateTimeOffset.Now);
            Refresh();
            _settings.Save();
        }
    }

    [RelayCommand(CanExecute = nameof(IsConnected))]
    private void Disconnect() => _cts?.Cancel();

    /// <summary>Opens a recording as the source and starts replaying it (used by the Recordings and Car history pages).</summary>
    public void StartReplay(string path)
    {
        if (IsConnected)
            return;

        SelectedSource = Sources.First(s => s.Kind == SourceKind.Replay);
        ReplayFile = path;
        if (ConnectCommand.CanExecute(null))
            ConnectCommand.Execute(null);
    }

    /// <summary>Saves the finished connection as a check, unless it is a simulator or replay session the user chose not to keep.</summary>
    private void SaveCheck()
    {
        var keep = SelectedSource.Kind switch
        {
            SourceKind.Elm or SourceKind.Slcan => true,
            SourceKind.Replay => _settings.SaveReplayChecks,
            _ => _settings.SaveSimulatorChecks,
        };
        if (!keep || _alerts is null || !CheckRecord.IsWorthSaving(_data))
            return;

        try
        {
            var check = CheckRecord.Create(_data, _health, BuyerItems.ToList(), _alerts.Current, _alerts.History(), _alerts.Coverage,
                SelectedSource.Title, _connectedAt, DateTimeOffset.Now, UseMiles, _chargeTest?.Result);
            _services.Database.AddCheck(check);
            EnqueueLog("Check saved; see Reports and Car history.");
            _services.NotifyDataChanged();
        }
        catch (Exception ex) when (ex is IOException or Microsoft.Data.Sqlite.SqliteException)
        {
            EnqueueLog($"Could not save the check: {ex.Message}");
        }
    }

    [RelayCommand]
    private void RefreshPorts()
    {
        var selected = SelectedPort;
        Ports.Clear();
        foreach (var port in SerialPort.GetPortNames().Distinct().OrderBy(p => p.Length).ThenBy(p => p, StringComparer.OrdinalIgnoreCase))
            Ports.Add(port);
        SelectedPort = selected is not null && Ports.Contains(selected) ? selected : Ports.FirstOrDefault();
    }

    [RelayCommand]
    private void BrowseReplay()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Open a CAN recording",
            Filter = "candump logs (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
            InitialDirectory = Directory.Exists(RecordingsFolder) ? RecordingsFolder : null,
        };
        if (dialog.ShowDialog() == true)
            ReplayFile = dialog.FileName;
    }

    /// <summary>Copies any text the view passes in (VIN, an alert); shows a short confirmation.</summary>
    [RelayCommand]
    private void Copy(string? text)
    {
        if (string.IsNullOrWhiteSpace(text) || text == Display.Missing)
            return;

        // The clipboard can be briefly locked by another application; retry a few times before giving up.
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                break;
            }
            catch (COMException) when (attempt < 4)
            {
                Thread.Sleep(50);
            }
            catch (COMException)
            {
                CopyFeedback = "Clipboard is busy; try again.";
                _copyFeedbackTimer.Start();
                return;
            }
        }

        var firstLine = text.Split('\n')[0].Trim();
        CopyFeedback = firstLine.Length > 40 ? "Copied " + firstLine[..40] + "…" : "Copied " + firstLine;
        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    [RelayCommand]
    private void CopySummary() => Copy(ReportWriter.ToText(_data, _health, UseMiles, _alerts?.Current, BuyerItems.ToList(), _chargeTest?.Result));

    [RelayCommand]
    private void CopyCarInfo() => Copy(CarInfo.ToText(CarInfo.WithEstimates(_data)));

    [RelayCommand]
    private void OpenRecordingsFolder()
    {
        Directory.CreateDirectory(RecordingsFolder);
        Process.Start(new ProcessStartInfo(RecordingsFolder) { UseShellExecute = true });
    }

    private bool CanExportReport() => _data.FrameCount > 0;

    [RelayCommand(CanExecute = nameof(CanExportReport))]
    private void ExportReport()
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save battery report",
            InitialDirectory = Directory.CreateDirectory(_services.ReportsFolder).FullName,
            Filter = "HTML report (*.html)|*.html",
            FileName = $"battery-report-{_data.Vin ?? "unknown-vin"}-{DateTime.Now:yyyyMMdd-HHmm}.html",
        };
        if (dialog.ShowDialog() != true)
            return;

        File.WriteAllText(dialog.FileName, ReportWriter.ToHtml(_data, _health, UseMiles, DateTimeOffset.Now, _alerts?.Current, _alerts?.History(), _alerts?.Coverage,
            BuyerItems.ToList(), _chargeTest?.Result));
        EnqueueLog($"Report saved: {dialog.FileName}");
        Process.Start(new ProcessStartInfo(dialog.FileName) { UseShellExecute = true });
        Refresh();
    }

    partial void OnIsRecordingChanged(bool value)
    {
        if (value)
        {
            try
            {
                _recorder = new CandumpWriter(Path.Combine(RecordingsFolder, $"capture-{DateTime.Now:yyyyMMdd-HHmmss}.log"));
                _services.Database.UpsertRecording(new RecordingRecord(_recorder.Path, _data.Vin, DateTimeOffset.Now, null, null, null, SelectedSource.Title, null));
                EnqueueLog($"Recording to {_recorder.Path}");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                EnqueueLog($"Cannot record: {ex.Message}");
                IsRecording = false;
            }
        }
        else if (_recorder is { } recorder)
        {
            _recorder = null;
            recorder.Dispose();
            var size = File.Exists(recorder.Path) ? new FileInfo(recorder.Path).Length : (long?)null;
            var existing = _services.Database.ListRecordings().FirstOrDefault(r => r.Path == recorder.Path);
            _services.Database.UpsertRecording(new RecordingRecord(recorder.Path, _monitor?.Snapshot().Vin, DateTimeOffset.Now, DateTimeOffset.Now,
                recorder.FrameCount, size, null, existing?.Notes));
            _services.NotifyDataChanged();
            EnqueueLog($"Recording saved: {recorder.Path}");
        }

        if (!IsConnected)
            Refresh();
    }

    partial void OnUseMilesChanged(bool value) => Refresh();

    /// <summary>Choosing a pack (or automatic) replaces any typed capacity; the choice is remembered per VIN.</summary>
    partial void OnSelectedPackChanged(PackOption value)
    {
        if (value.Key != PackOption.Custom)
            OriginalCapacityText = "";
        SaveCarChoice();
        Refresh();
    }

    partial void OnOriginalCapacityTextChanged(string value)
    {
        if (value.Trim().Length == 0 || ParseOriginalCapacity() is not null)
            SaveCarChoice();
        Refresh();
    }

    /// <summary>Stores the current pack choice and typed capacity for the connected car.</summary>
    private void SaveCarChoice()
    {
        if (_capacityVin is not null)
            _services.Database.SetCarChoice(_capacityVin, SelectedPack.IsPack ? SelectedPack.Key : null, ParseOriginalCapacity());
    }

    private ICanAdapter CreateAdapter() => SelectedSource.Kind switch
    {
        SourceKind.Elm => new ElmAdapter(() => new SerialPortLink(SelectedPort!, SelectedBaud)),
        SourceKind.Slcan => new SlcanAdapter(() => new SerialPortLink(SelectedPort!, 115200)),
        SourceKind.Replay => new ReplayAdapter(ReplayFile!),
        SourceKind.SimulatorModelS => new SimulatorAdapter(SimulatedCar.ModelS),
        SourceKind.SimulatorModel3Charging => new SimulatorAdapter(SimulatedCar.Model3Charging),
        SourceKind.SimulatorModelSCharging => new SimulatorAdapter(SimulatedCar.ModelSCharging),
        _ => new SimulatorAdapter(SimulatedCar.Model3),
    };

    private void EnqueueLog(string message) => _pendingLog.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");

    private double? ParseOriginalCapacity()
    {
        var text = OriginalCapacityText.Trim();
        if (text.Length == 0)
        {
            OriginalCapacityError = null;
            return null;
        }

        if ((double.TryParse(text, NumberStyles.Float, CultureInfo.CurrentCulture, out var kwh)
             || double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out kwh))
            && kwh is >= 10 and <= 250)
        {
            OriginalCapacityError = null;
            return kwh;
        }

        OriginalCapacityError = "Enter the capacity in kWh, e.g. 80.5";
        return null;
    }

    private void Refresh()
    {
        if (_refreshing)
            return;

        _refreshing = true;
        try
        {
            RefreshAll();
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void RefreshAll()
    {
        while (_pendingLog.TryDequeue(out var line))
        {
            Log.Insert(0, line);
            if (Log.Count > MaxLogLines)
                Log.RemoveAt(Log.Count - 1);
        }

        var data = _monitor?.Snapshot() ?? new BatteryData();
        _chargeTest = _monitor?.ChargeTestStatus();
        LoadRememberedCapacity(data);
        var health = HealthCalculator.Evaluate(data, ParseOriginalCapacity(), SelectedPack.IsPack ? SelectedPack.Key : null);
        var exportChanged = CanExportReport() != data.FrameCount > 0;
        _data = data;
        _health = health;
        if (exportChanged)
            ExportReportCommand.NotifyCanExecuteChanged();

        RefreshHealth(health);
        RefreshVehicle(data);
        RefreshLive(data, health);
        RefreshBricks(data);
        RefreshNotes(health, data);
        RefreshAlerts(data, health);
        RefreshBuyerCheck(data, health);
        RefreshCarInfo(data);
        RefreshStatus(data);
        Refreshed?.Invoke(this, EventArgs.Empty);
    }

    private void RefreshBuyerCheck(BatteryData data, HealthReport health)
    {
        if (data.FrameCount == 0)
            return;

        var items = BuyerCheck.Evaluate(new CheckInput(
            data,
            health,
            _alerts?.Current ?? [],
            _alerts?.History() ?? [],
            _alerts?.Coverage ?? new AlertCoverage(false, 0, 0),
            DateTimeOffset.Now));
        if (items.SequenceEqual(BuyerItems))
            return;

        BuyerItems.Clear();
        foreach (var item in items)
            BuyerItems.Add(item);
        BuyerSummary = BuyerCheck.Summarize(items) + ". Hover an item for how it was judged; the report has the details and a checklist for what the tool cannot see.";
    }

    private void RefreshCarInfo(BatteryData data)
    {
        var values = CarInfo.WithEstimates(data);
        var shown = CarInfo.ToText(values);
        if (shown == _carInfoShown)
            return;

        _carInfoShown = shown;
        InfotainmentInfo.Clear();
        ConfigurationInfo.Clear();
        foreach (var field in CarInfo.Fields.Where(f => values.ContainsKey(f.Key)))
        {
            var target = field.Group == CarInfo.Infotainment ? InfotainmentInfo : ConfigurationInfo;
            target.Add(new Metric(field.Label, field.Hint) { Value = values[field.Key] });
        }

        HasCarInfo = values.Count > 0;
    }

    private void RefreshAlerts(BatteryData data, HealthReport health)
    {
        if (_alerts is null)
            return;

        if (IsConnected)
            _alerts.Update(data, health, DateTimeOffset.Now);

        AlertSummary = _alerts.Coverage switch
        {
            { Supported: true, MessagesReceived: 0 } => "Waiting for the car's alert messages…",
            { Supported: true } c => $"Alert messages received from {c.MessagesReceived} of {c.MessagesKnown} controllers, plus this tool's battery checks.",
            _ when data.Platform == VehiclePlatform.LegacyModelSX => "Pre-2021 Model S/X alert messages are not publicly decoded, so only this tool's battery checks are shown.",
            _ when IsConnected => "Identifying the car…",
            _ => "Not connected. Showing the last session.",
        };

        if (_alerts.Version == _alertsVersion)
            return;

        _alertsVersion = _alerts.Version;
        CurrentAlerts.Clear();
        foreach (var alert in _alerts.Current)
            CurrentAlerts.Add(AlertItem.From(alert));
        AlertHistory.Clear();
        foreach (var entry in _alerts.History())
            AlertHistory.Add(AlertItem.From(entry));

        HasCurrentAlerts = CurrentAlerts.Count > 0;
        HasAlertHistory = AlertHistory.Count > 0;
        AlertsHeader = CurrentAlerts.Count == 0 ? "Alerts" : $"Alerts ({CurrentAlerts.Count})";
    }

    /// <summary>Once the car's VIN is known, binds the capacity override to it and fills in any value remembered for it.</summary>
    private void LoadRememberedCapacity(BatteryData data)
    {
        if (data.Vin is not { } vin || vin == _capacityVin)
            return;

        _capacityVin = vin;
        var info = new VinInfo(vin);
        _services.Database.TouchCar(vin, ReportWriter.ModelName(info.Model), info.ModelYear, DateTimeOffset.Now);
        var car = _services.Database.GetCar(vin);
        if (ParseOriginalCapacity() is not null || SelectedPack.IsPack)
        {
            SaveCarChoice(); // chosen before the VIN arrived
        }
        else if (car?.OriginalKWh is { } kwh)
        {
            SelectedPack = PackOptions.First(o => o.Key == PackOption.Custom);
            OriginalCapacityText = kwh.ToString("0.0", CultureInfo.CurrentCulture);
            EnqueueLog($"Using the original capacity remembered for {vin}: {Display.KWh(kwh)}.");
        }
        else if (car?.PackKey is { } packKey && PackOptions.FirstOrDefault(o => o.Key == packKey) is { } pack)
        {
            SelectedPack = pack;
            EnqueueLog($"Using the battery pack remembered for {vin}: {pack.Title}.");
        }
    }

    private string ExplainPack(HealthReport health)
    {
        if (SelectedPack.Key == PackOption.Custom)
            return "Enter the capacity the car reported when new (its nominal full pack, e.g. an early ScanMyTesla reading). Remembered for this VIN.";

        if (health.Pack is { } chosen && health.OriginalSource == CapacitySource.PackChosen)
            return $"Typically {chosen.NewKWh:0.0} kWh when new (±{chosen.TolerancePercent:0}%). {chosen.Basis} Remembered for this VIN.";

        if (health.PackEstimate is not { } estimate)
            return "The pack is estimated once the VIN, odometer and today's capacity have been read.";

        var text = $"Likely {estimate.Pack.Name}: typically {estimate.Pack.NewKWh:0.0} kWh when new (±{estimate.Pack.TolerancePercent:0}%). Based on: {estimate.Reason}.";
        if (!estimate.Confident)
            text += $" Could also be {string.Join(" or ", estimate.Alternatives.Select(p => p.Name))}.";
        return text + " Choose the pack if you know it.";
    }

    private void RefreshHealth(HealthReport health)
    {
        SohValue = health.StateOfHealthPercent ?? 0;
        SohText = Display.Percent(health.StateOfHealthPercent);
        Rating = health.Rating;
        RatingText = health.Rating == HealthRating.Unknown && health.CurrentKWh is not null
            ? "Original capacity needed"
            : Display.Rating(health.Rating);

        // Pre-2021 Model S/X: pick (or let the tool estimate) the pack. Other cars: ask only if the BMS never reports it.
        ShowPackPicker = _data.Platform == VehiclePlatform.LegacyModelSX;
        ShowCapacityPrompt = !ShowPackPicker && health.CurrentKWh is not null && health.OriginalSource != CapacitySource.Bms;
        CapacityPromptText = "The BMS has not reported it yet. Enter it only if it never appears.";
        PackExplanation = ExplainPack(health);

        _degradation.Value = Display.Percent(health.DegradationPercent);
        _original.Value = Display.KWh(health.OriginalKWh);
        _original.Detail = health switch
        {
            { OriginalKWh: null } => null,
            { Pack: { } pack, OriginalSource: CapacitySource.Estimated } => $"estimated: {pack.Name.Split(' ')[0]} kWh pack",
            { Pack: { } pack } => $"chosen: {pack.Name.Split(' ')[0]} kWh pack",
            _ => Display.Source(health.OriginalSource),
        };
        _current.Value = Display.KWh(health.CurrentKWh);
        _lost.Value = Display.KWh(health.LostKWh);
        _usable.Value = Display.KWh(health.UsableKWh);
        _range.Value = Display.Distance(health.FullRangeKm, UseMiles);
    }

    private void RefreshVehicle(BatteryData data)
    {
        var vin = data.Vin is { } v ? new VinInfo(v) : null;
        VinText = data.Vin ?? Display.Missing;
        VinWarning = vin?.CheckDigitValid == false ? "VIN check digit does not match: the VIN may have been read incorrectly." : null;
        ModelText = vin is null ? Display.Missing : ReportWriter.ModelName(vin.Model);
        YearText = vin?.ModelYear?.ToString(CultureInfo.InvariantCulture) ?? Display.Missing;
        PlantText = vin?.Plant ?? Display.Missing;
        OdometerText = Display.Distance(data.OdometerKm, UseMiles, "N0");
        PlatformText = _monitor?.Profile.Name ?? Display.Missing;

        var energyId = VehicleProfiles.EnergyMessageId(data.Platform);
        LayoutText = _monitor is null || data.Platform == VehiclePlatform.Auto
            ? Display.Missing
            : data.SeenIds.Contains(energyId)
                ? $"0x{energyId:X3}, {EnergyStatus.Describe(data.EnergyLayout)}"
                : $"Waiting for 0x{energyId:X3}";

    }

    private void RefreshLive(BatteryData data, HealthReport health)
    {
        _soc.Value = Display.Percent(data.SocUiPercent);
        _usableLeft.Value = Display.KWh(health.UsableRemainingKWh);
        _buffer.Value = Display.KWh(data.EnergyBufferKWh);
        _toLimit.Value = Display.KWh(data.EnergyToChargeCompleteKWh);
        _voltage.Value = Display.Volts(data.PackVoltage);
        _currentAmps.Value = Display.Amps(data.PackCurrent);
        _power.Value = Display.Kilowatts(data.PackVoltage * data.PackCurrent / 1000);
        _temperature.Value = data.TempMinC is null && data.TempMaxC is null
            ? Display.Missing
            : $"{Display.Number(data.TempMinC, "0.0")} – {Display.Celsius(data.TempMaxC)}";
        _spread.Value = Display.Millivolts(health.CellSpreadMv);
        _charged.Value = Display.KWh(data.ChargeTotalKWh, "N0");
        _discharged.Value = Display.KWh(data.DischargeTotalKWh, "N0");
        _cycles.Value = Display.Number(health.EquivalentFullCycles);
    }

    private void RefreshBricks(BatteryData data)
    {
        var voltages = data.BrickVoltages;
        HasBricks = voltages.Count > 0;
        if (voltages.Count == 0)
        {
            Bricks.Clear();
            BrickSummary = data.BrickVoltageMin is not null
                ? $"Lowest {Display.Volts(data.BrickVoltageMin, "0.000")} (#{data.BrickNumberMin}), highest {Display.Volts(data.BrickVoltageMax, "0.000")} (#{data.BrickNumberMax}). Waiting for per-cell data."
                : "No per-cell data yet.";
            return;
        }

        if (Bricks.Count != voltages.Count || !Bricks.Select(b => b.Index).SequenceEqual(voltages.Keys))
        {
            Bricks.Clear();
            foreach (var index in voltages.Keys)
                Bricks.Add(new BrickBar(index));
        }

        var low = voltages.Values.Min();
        var high = voltages.Values.Max();
        var span = Math.Max(high - low, 0.001);
        foreach (var bar in Bricks)
        {
            var volts = voltages[bar.Index];
            bar.Height = BrickBar.MinHeight + (BrickBar.MaxHeight - BrickBar.MinHeight) * (volts - low) / span;
            bar.IsLowest = volts == low;
            bar.IsHighest = volts == high;
            bar.ToolTip = $"Cell group {bar.Index + 1}: {volts.ToString("0.0000", CultureInfo.CurrentCulture)} V";
        }

        var lowest = voltages.First(kv => kv.Value == low).Key + 1;
        var highest = voltages.First(kv => kv.Value == high).Key + 1;
        BrickSummary = $"{voltages.Count} cell groups. Lowest {Display.Volts(low, "0.000")} (#{lowest}), highest {Display.Volts(high, "0.000")} (#{highest}). Bars are scaled between the two.";
    }

    private void RefreshNotes(HealthReport health, BatteryData data)
    {
        IEnumerable<string> notes = health.Notes;
        if (_monitor is null && data.FrameCount == 0)
        {
            notes =
            [
                "Plug the adapter into the car's diagnostic connector, wake the car (open a door or sit inside) and press Connect.",
                "No car nearby? Pick \"Simulator (no car)\" as the source to see how the tool works.",
            ];
        }
        else if (IsConnected && health.CurrentKWh is null)
        {
            notes = notes.Prepend("Collecting data. Capacity figures usually appear within a few seconds.");
        }

        if (!Notes.SequenceEqual(notes))
        {
            Notes.Clear();
            foreach (var note in notes)
                Notes.Add(note);
        }
    }

    private void RefreshStatus(BatteryData data)
    {
        var now = DateTime.UtcNow;
        var elapsed = (now - _rateSampledAt).TotalSeconds;
        if (elapsed >= 1)
        {
            _framesPerSecond = (data.FrameCount - _rateFrames) / elapsed;
            _rateFrames = data.FrameCount;
            _rateSampledAt = now;
        }

        FrameText = data.FrameCount == 0
            ? ""
            : $"{data.FrameCount:N0} frames · {(IsConnected ? _framesPerSecond : 0):N0}/s · {data.SeenIds.Count} message IDs"
              + (_recorder is null ? "" : " · recording");
    }
}

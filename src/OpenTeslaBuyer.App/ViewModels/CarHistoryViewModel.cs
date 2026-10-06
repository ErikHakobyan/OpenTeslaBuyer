using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Storage;

namespace OpenTeslaBuyer.App.ViewModels;

/// <summary>Every car checked so far; for the selected one, its health over time, checks, alert history and recordings.</summary>
public sealed partial class CarHistoryViewModel(AppServices services, ShellViewModel shell) : ObservableObject, IPage
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    private CarRow? _selectedCar;

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string _notes = "";

    [ObservableProperty]
    private string _healthTrend = "";

    [ObservableProperty]
    private IReadOnlyList<double> _healthPoints = [];

    [ObservableProperty]
    private string _choiceText = "";

    [ObservableProperty]
    private string? _feedback;

    [ObservableProperty]
    private string? _alertsNote;

    [ObservableProperty]
    private string? _recordingsNote;

    private bool _loadingNotes;

    public ObservableCollection<CarRow> Cars { get; } = [];

    public ObservableCollection<CheckRow> Checks { get; } = [];

    public ObservableCollection<AlertItem> Alerts { get; } = [];

    public ObservableCollection<RecordingItem> Recordings { get; } = [];

    public bool HasSelection => SelectedCar is not null;

    public void Activate()
    {
        var selectedVin = SelectedCar?.Summary.Car.Vin;
        Cars.Clear();
        foreach (var car in services.Database.ListCars())
            Cars.Add(new CarRow(car));

        Summary = Cars.Count == 0
            ? "No cars yet. Cars appear here once their VIN has been read."
            : $"{Cars.Count} car(s). Select one to see its history.";
        SelectedCar = Cars.FirstOrDefault(c => c.Summary.Car.Vin == selectedVin) ?? Cars.FirstOrDefault();
    }

    partial void OnSelectedCarChanged(CarRow? value)
    {
        Checks.Clear();
        Alerts.Clear();
        Recordings.Clear();
        Feedback = null;
        if (value is null)
            return;

        var car = value.Summary.Car;
        var checks = services.Database.ListChecks(car.Vin);
        foreach (var check in checks)
            Checks.Add(new CheckRow(check));

        var points = checks.Where(c => c.StateOfHealthPercent is not null).OrderBy(c => c.Ended).ToList();
        HealthPoints = points.Select(c => c.StateOfHealthPercent!.Value).ToList();
        HealthTrend = points.Count switch
        {
            0 => "No health readings saved yet.",
            1 => $"{Pct(points[0].StateOfHealthPercent)} on {Ui.When(points[0].Ended)}.",
            _ => $"{Pct(points[0].StateOfHealthPercent)} → {Pct(points[^1].StateOfHealthPercent)} over {(points[^1].Ended - points[0].Ended).TotalDays:0} days"
                 + (points[0].OdometerKm is { } first && points[^1].OdometerKm is { } last ? $" and {(last - first).ToString("N0", CultureInfo.CurrentCulture)} km." : "."),
        };

        foreach (var entry in AlertTracker.Summarize(services.Database.LoadEpisodes(car.Vin)))
            Alerts.Add(AlertItem.From(entry));

        foreach (var recording in services.Database.ListRecordings(car.Vin))
            Recordings.Add(new RecordingItem(recording, car, r => services.Database.UpsertRecording(r.Record with { Notes = r.Notes })));

        AlertsNote = Alerts.Count == 0 ? "No alerts recorded for this car." : null;
        RecordingsNote = Recordings.Count == 0 ? "No recordings for this car. Press Record on the Diagnostics page while connected." : null;

        ChoiceText = car switch
        {
            { OriginalKWh: { } kwh } => $"Capacity when new entered as {Display.KWh(kwh)}.",
            { PackKey: { } key } when PackEstimator.Find(key) is { } pack => $"Battery pack chosen: {pack.Name}.",
            _ => "",
        };

        _loadingNotes = true;
        Notes = car.Notes ?? "";
        _loadingNotes = false;
    }

    partial void OnNotesChanged(string value)
    {
        if (!_loadingNotes && SelectedCar is { } car)
            services.Database.SetCarNotes(car.Summary.Car.Vin, value);
    }

    [RelayCommand]
    private void CopyVin()
    {
        if (SelectedCar is { } car)
            Feedback = Ui.Copy(car.Summary.Car.Vin) ? "VIN copied." : "Clipboard is busy; try again.";
    }

    [RelayCommand]
    private void OpenReport(CheckRow row)
    {
        if (services.Database.GetCheckReport(row.Record.Id) is not { } html)
            return;

        System.IO.Directory.CreateDirectory(services.ReportsFolder);
        var path = System.IO.Path.Combine(services.ReportsFolder, $"battery-report-{row.Record.Vin}-{row.Record.Ended.ToLocalTime():yyyyMMdd-HHmm}.html");
        System.IO.File.WriteAllText(path, html);
        Ui.Open(path);
    }

    [RelayCommand]
    private void Replay(RecordingItem item)
    {
        if (item.Exists)
            shell.ReplayRecording(item.Record.Path);
    }

    [RelayCommand]
    private void DeleteCar()
    {
        if (SelectedCar is not { } car)
            return;

        if (!Ui.Confirm($"Delete {car.Summary.Car.Vin} with its {car.Summary.CheckCount} saved check(s) and alert history? Recording files are kept.", "Delete car"))
            return;

        services.Database.DeleteCar(car.Summary.Car.Vin);
        SelectedCar = null;
        Activate();
    }

    private static string Pct(double? value) => value is { } v ? v.ToString("0.0", CultureInfo.CurrentCulture) + "%" : Display.Missing;
}

/// <summary>A car in the list.</summary>
public sealed class CarRow(CarSummary summary)
{
    public CarSummary Summary { get; } = summary;

    public string Title { get; } = summary.Car.Model is { } model ? $"{model} {summary.Car.ModelYear}" : "Tesla";

    public string Vin { get; } = summary.Car.Vin;

    public string Meta { get; } = summary.Latest?.StateOfHealthPercent is { } soh
        ? $"{soh.ToString("0.0", CultureInfo.CurrentCulture)}% health · {summary.CheckCount} check(s) · last {Ui.When(summary.Car.LastSeen)}"
        : $"{summary.CheckCount} check(s) · last seen {Ui.When(summary.Car.LastSeen)}";
}

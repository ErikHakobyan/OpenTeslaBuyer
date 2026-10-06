using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Storage;

namespace OpenTeslaBuyer.App.ViewModels;

/// <summary>Every car checked so far; for the selected one, its trends, tests, checks, alert history and recordings.</summary>
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
    private string _standingOutNote = "";

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

    /// <summary>One small chart per measurement followed across the car's checks.</summary>
    public ObservableCollection<TrendTile> Trends { get; } = [];

    /// <summary>Cell groups the charging or overnight tests flagged, the most often first.</summary>
    public ObservableCollection<string> StandingOut { get; } = [];

    /// <summary>The latest charger and 12 V results.</summary>
    public ObservableCollection<string> LatestTests { get; } = [];

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
        Trends.Clear();
        StandingOut.Clear();
        LatestTests.Clear();
        Feedback = null;
        if (value is null)
            return;

        var car = value.Summary.Car;
        var checks = services.Database.ListChecks(car.Vin);
        foreach (var check in checks)
            Checks.Add(new CheckRow(check));

        foreach (var trend in CarTrends.Build(checks))
            Trends.Add(new TrendTile(trend.Title, trend.Latest, trend.Change, trend.Points.Select(p => p.Value).ToList(), trend.Unit, trend.Format));

        foreach (var group in CarTrends.Groups(checks).Take(10))
            StandingOut.Add(group.Describe());
        var tested = checks.Any(c => c.Tests is { Resistance: not null } or { Overnight: not null });
        StandingOutNote = StandingOut.Count > 0
            ? "A group flagged in more than one test is worth showing to a service centre."
            : tested ? "No cell group has stood out in this car's charging or overnight tests." : "No charging or overnight tests saved for this car yet.";

        if (checks.FirstOrDefault(c => c.Tests?.Charger is not null) is { Tests: { } charger } chargerCheck)
            LatestTests.Add($"Onboard charger, {Ui.When(chargerCheck.Ended)}: {charger.ChargerSummary}");
        if (checks.FirstOrDefault(c => c.Tests?.TwelveVolt is not null) is { Tests: { } twelveVolt } twelveVoltCheck)
            LatestTests.Add($"12 V system, {Ui.When(twelveVoltCheck.Ended)}: {twelveVolt.TwelveVoltSummary}");

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

}

/// <summary>One measurement's chart on the car's page.</summary>
public sealed record TrendTile(string Title, string Latest, string Change, IReadOnlyList<double> Values, string Unit, string Format);

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

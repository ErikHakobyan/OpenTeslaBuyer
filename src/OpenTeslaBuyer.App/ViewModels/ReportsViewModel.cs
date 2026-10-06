using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Storage;

namespace OpenTeslaBuyer.App.ViewModels;

/// <summary>Every saved check, with its full report kept in the database so it can be opened again without the car.</summary>
public sealed partial class ReportsViewModel(AppServices services) : ObservableObject, IPage
{
    private IReadOnlyList<CheckRecord> _all = [];

    [ObservableProperty]
    private string _filter = "";

    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private string? _feedback;

    [ObservableProperty]
    private bool _isEmpty;

    public ObservableCollection<CheckRow> Items { get; } = [];

    public void Activate()
    {
        _all = services.Database.ListChecks();
        ApplyFilter();
    }

    partial void OnFilterChanged(string value) => ApplyFilter();

    [RelayCommand]
    private void Open(CheckRow row)
    {
        if (services.Database.GetCheckReport(row.Record.Id) is not { } html)
            return;

        Directory.CreateDirectory(services.ReportsFolder);
        var path = Path.Combine(services.ReportsFolder, FileName(row.Record));
        File.WriteAllText(path, html);
        Ui.Open(path);
    }

    [RelayCommand]
    private void Export(CheckRow row)
    {
        if (services.Database.GetCheckReport(row.Record.Id) is not { } html)
            return;

        var dialog = new SaveFileDialog
        {
            Title = "Save report",
            Filter = "HTML report (*.html)|*.html",
            FileName = FileName(row.Record),
            InitialDirectory = Directory.CreateDirectory(services.ReportsFolder).FullName,
        };
        if (dialog.ShowDialog() == true)
            File.WriteAllText(dialog.FileName, html);
    }

    [RelayCommand]
    private void CopySummary(CheckRow row)
    {
        if (row.Record.SummaryText is { } text)
            Feedback = Ui.Copy(text) ? $"Copied the summary of the check from {row.Title}." : "Clipboard is busy; try again.";
    }

    [RelayCommand]
    private void Delete(CheckRow row)
    {
        if (!Ui.Confirm($"Delete the check from {row.Title} ({row.Car})?", "Delete check"))
            return;

        services.Database.DeleteCheck(row.Record.Id);
        services.NotifyDataChanged();
    }

    [RelayCommand]
    private void OpenFolder() => Ui.OpenFolder(services.ReportsFolder);

    private void ApplyFilter()
    {
        var filter = Filter.Trim();
        Items.Clear();
        foreach (var check in _all.Where(c => filter.Length == 0
                                              || (c.Vin?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                                              || (c.Model?.Contains(filter, StringComparison.OrdinalIgnoreCase) ?? false)
                                              || c.Source.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            Items.Add(new CheckRow(check));
        }

        IsEmpty = Items.Count == 0;
        Summary = _all.Count == 0
            ? "No saved checks yet. A check is saved when you disconnect from a car (simulator and replay sessions only if enabled in Settings)."
            : $"{Items.Count} of {_all.Count} check(s). Each keeps its full report, so it can be opened again without the car.";
    }

    private static string FileName(CheckRecord check) =>
        $"battery-report-{check.Vin ?? "unknown-vin"}-{check.Ended.ToLocalTime():yyyyMMdd-HHmm}.html";
}

/// <summary>One saved check in a list.</summary>
public sealed class CheckRow(CheckRecord record)
{
    public CheckRecord Record { get; } = record;

    public string Title { get; } = Ui.When(record.Ended);

    public string Car { get; } = record.Vin is null ? "VIN not read" : $"{record.Vin}{(record.Model is { } m ? $" · {m} {record.ModelYear}" : "")}";

    public string Health { get; } = record.StateOfHealthPercent is { } soh
        ? $"{soh.ToString("0.0", CultureInfo.CurrentCulture)}% health · {Display.KWh(record.CurrentKWh)} of {Display.KWh(record.OriginalKWh)}"
        : $"Health not available · {Display.KWh(record.CurrentKWh)}";

    public string Meta { get; } = string.Join(" · ", new[]
    {
        record.OdometerKm is { } km ? $"{km.ToString("N0", CultureInfo.CurrentCulture)} km" : null,
        record.CellSpreadMv is { } mv ? $"cell spread {mv:0} mV" : null,
        record.ActiveAlerts > 0 ? $"{record.ActiveAlerts} active alert(s)" : null,
        record.BuyerSummary is { } buyer ? $"buyer check: {buyer}" : null,
        record.Source,
    }.Where(p => p is not null));
}

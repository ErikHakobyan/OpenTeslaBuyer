using System.Collections.ObjectModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using OpenTeslaBuyer.Core.Storage;

namespace OpenTeslaBuyer.App.ViewModels;

/// <summary>The recordings library: CAN logs saved with Record, plus any log files found in the recordings folder.</summary>
public sealed partial class RecordingsViewModel(AppServices services, ShellViewModel shell) : ObservableObject, IPage
{
    [ObservableProperty]
    private string _summary = "";

    [ObservableProperty]
    private bool _isEmpty;

    public ObservableCollection<RecordingItem> Items { get; } = [];

    public string Folder => services.RecordingsFolder;

    public void Activate()
    {
        AddUnlistedFiles();
        var cars = services.Database.ListCars().ToDictionary(c => c.Car.Vin, c => c.Car);
        Items.Clear();
        foreach (var record in services.Database.ListRecordings())
            Items.Add(new RecordingItem(record, cars.GetValueOrDefault(record.Vin ?? ""), SaveNotes));

        IsEmpty = Items.Count == 0;
        var total = Items.Sum(i => i.Record.SizeBytes ?? 0);
        Summary = IsEmpty
            ? "No recordings yet. Press Record on the Diagnostics page while connected; recordings appear here."
            : $"{Items.Count} recording(s), {Ui.Size(total)} in {Folder}";
        OnPropertyChanged(nameof(Folder));
    }

    [RelayCommand]
    private void Replay(RecordingItem item)
    {
        if (item.Exists)
            shell.ReplayRecording(item.Record.Path);
    }

    [RelayCommand]
    private void ShowInFolder(RecordingItem item) => Ui.ShowInFolder(item.Record.Path);

    [RelayCommand]
    private void Delete(RecordingItem item)
    {
        if (!Ui.Confirm($"Delete the recording {Path.GetFileName(item.Record.Path)}? The file is removed from disk.", "Delete recording"))
            return;

        if (File.Exists(item.Record.Path))
            File.Delete(item.Record.Path);
        services.Database.DeleteRecording(item.Record.Path);
        Activate();
    }

    [RelayCommand]
    private void OpenFolder() => Ui.OpenFolder(Folder);

    /// <summary>Adds log files from elsewhere (e.g. recorded by another tool in candump format) to the library, where they are.</summary>
    [RelayCommand]
    private void Import()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Add CAN recordings",
            Filter = "candump logs (*.log;*.txt)|*.log;*.txt|All files (*.*)|*.*",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true)
            return;

        foreach (var file in dialog.FileNames)
            Register(file, "Imported");
        Activate();
    }

    private void SaveNotes(RecordingItem item) =>
        services.Database.UpsertRecording(item.Record with { Notes = string.IsNullOrWhiteSpace(item.Notes) ? null : item.Notes });

    /// <summary>Log files in the recordings folder that the database does not know yet (e.g. from before the library existed).</summary>
    private void AddUnlistedFiles()
    {
        if (!Directory.Exists(Folder))
            return;

        var known = services.Database.ListRecordings().Select(r => r.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.GetFiles(Folder, "*.log").Where(f => !known.Contains(f)))
            Register(file, "Found in the recordings folder");
    }

    private void Register(string file, string source)
    {
        var info = new FileInfo(file);
        services.Database.UpsertRecording(new RecordingRecord(file, null, info.CreationTime, info.LastWriteTime, null, info.Length, source, null));
    }
}

/// <summary>One recording in the list.</summary>
public sealed partial class RecordingItem : ObservableObject
{
    private readonly Action<RecordingItem> _saveNotes;

    [ObservableProperty]
    private string _notes;

    public RecordingItem(RecordingRecord record, CarRecord? car, Action<RecordingItem> saveNotes)
    {
        Record = record;
        _saveNotes = saveNotes;
        _notes = record.Notes ?? "";
        Exists = File.Exists(record.Path);
        Title = Ui.When(record.Started);
        Car = record.Vin is null ? "Car unknown" : $"{record.Vin}{(car?.Model is { } model ? $" · {model} {car.ModelYear}" : "")}";

        var parts = new List<string>();
        if (record.Ended is { } ended && ended > record.Started)
            parts.Add(Ui.Duration(ended - record.Started));
        if (record.Frames is { } frames)
            parts.Add($"{frames:N0} frames");
        if (record.SizeBytes is { } size)
            parts.Add(Ui.Size(size));
        if (record.Source is { } source)
            parts.Add(source);
        if (!Exists)
            parts.Add("file missing");
        Meta = string.Join(" · ", parts);
    }

    public RecordingRecord Record { get; }

    public bool Exists { get; }

    public string Title { get; }

    public string Car { get; }

    public string Meta { get; }

    public string FileName => Path.GetFileName(Record.Path);

    partial void OnNotesChanged(string value) => _saveNotes(this);
}

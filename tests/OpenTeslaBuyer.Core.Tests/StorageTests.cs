using OpenTeslaBuyer.Core.Adapters;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Battery;
using OpenTeslaBuyer.Core.Buyer;
using OpenTeslaBuyer.Core.Storage;
using OpenTeslaBuyer.Core.Vehicles;

namespace OpenTeslaBuyer.Core.Tests;

public sealed class StorageTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 5, 12, 0, 0, TimeSpan.Zero);
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "tbh-db-" + Guid.NewGuid().ToString("N"));

    private string DatabasePath => Path.Combine(_folder, AppDatabase.FileName);

    [Fact]
    public void Remembers_cars_and_choices_across_reopening()
    {
        using (var db = new AppDatabase(DatabasePath))
        {
            db.TouchCar("5YJSA1E24FF100456", "Model S", 2015, T0);
            db.SetCarChoice("5YJSA1E24FF100456", "85", null);
            db.SetCarNotes("5YJSA1E24FF100456", "Seller says pack replaced in 2021");
            db.TouchCar("5YJSA1E24FF100456", null, null, T0.AddDays(1));
        }

        using var reopened = new AppDatabase(DatabasePath);
        var car = reopened.GetCar("5YJSA1E24FF100456")!;
        Assert.Equal("Model S", car.Model);
        Assert.Equal(2015, car.ModelYear);
        Assert.Equal("85", car.PackKey);
        Assert.Equal(T0, car.FirstSeen);
        Assert.Equal(T0.AddDays(1), car.LastSeen);
        Assert.Equal("Seller says pack replaced in 2021", car.Notes);
    }

    [Fact]
    public void Stores_checks_with_their_report()
    {
        using var db = new AppDatabase(DatabasePath);
        var first = db.AddCheck(Check("5YJ3E1EB8JF100123", T0, soh: 90.1));
        var second = db.AddCheck(Check("5YJ3E1EB8JF100123", T0.AddDays(30), soh: 89.4));
        db.AddCheck(Check(null, T0.AddDays(2), soh: null));

        var forCar = db.ListChecks("5YJ3E1EB8JF100123");
        var cars = db.ListCars();

        Assert.Equal([second, first], forCar.Select(c => c.Id));
        Assert.Null(forCar[0].ReportHtml); // listing skips the large column
        Assert.Equal("<html>report</html>", db.GetCheckReport(first));
        Assert.Equal(3, db.ListChecks().Count);
        var summary = Assert.Single(cars);
        Assert.Equal(2, summary.CheckCount);
        Assert.Equal(89.4, summary.Latest!.StateOfHealthPercent);

        db.DeleteCheck(first);
        Assert.Single(db.ListChecks("5YJ3E1EB8JF100123"));
    }

    [Fact]
    public void Alert_history_round_trips_through_the_tracker()
    {
        using var db = new AppDatabase(DatabasePath);
        var store = new DatabaseAlertHistoryStore(db);
        var data = new BatteryData { Vin = "5YJ3E1EB8JF100123" };
        var health = HealthCalculator.Evaluate(data);
        data.CarAlerts["PCS_a019"] = true;

        var first = new AlertTracker(store);
        first.Update(data, health, T0);
        first.Update(data, health, T0.AddSeconds(5));
        first.EndSession(T0.AddMinutes(1));

        var second = new AlertTracker(store);
        second.Update(new BatteryData { Vin = data.Vin }, health, T0.AddDays(1));

        var entry = Assert.Single(second.History());
        Assert.Equal("PCS_a019", entry.Definition.Code);
        Assert.True(db.LoadEpisodes(data.Vin).Single().EndedWithSession);
    }

    [Fact]
    public void Recording_updates_keep_what_is_already_known()
    {
        using var db = new AppDatabase(DatabasePath);
        db.UpsertRecording(new RecordingRecord(@"C:\rec\a.log", "5YJ3E1EB8JF100123", T0, null, null, null, "OBDLink / ELM327", null));
        db.UpsertRecording(new RecordingRecord(@"C:\rec\a.log", null, T0, T0.AddMinutes(2), 4_200, 180_000, null, "first drive"));

        var recording = Assert.Single(db.ListRecordings());

        Assert.Equal("5YJ3E1EB8JF100123", recording.Vin);
        Assert.Equal(T0.AddMinutes(2), recording.Ended);
        Assert.Equal(4_200, recording.Frames);
        Assert.Equal("OBDLink / ELM327", recording.Source);
        Assert.Equal("first drive", recording.Notes);
        Assert.Single(db.ListRecordings("5YJ3E1EB8JF100123"));

        db.DeleteRecording(@"C:\rec\a.log");
        Assert.Empty(db.ListRecordings());
    }

    [Fact]
    public void Imports_data_from_earlier_versions()
    {
        var jsonFolder = Path.Combine(_folder, "AlertHistory");
        new AlertHistoryStore(jsonFolder).Save("5YJSA1E24FF100456", [new AlertEpisode { Code = "TOOL_w002", Start = T0, End = T0.AddMinutes(3) }]);
        using var db = new AppDatabase(DatabasePath);

        var imported = LegacyImport.ImportAlertHistory(db, jsonFolder);
        LegacyImport.ImportCarChoices(db, new Dictionary<string, double> { ["5YJSA1E24FF100456"] = 75.0 }, new Dictionary<string, string> { ["5YJ3E1EB8JF100123"] = "75" });

        Assert.Equal(1, imported);
        Assert.Equal("TOOL_w002", db.LoadEpisodes("5YJSA1E24FF100456").Single().Code);
        Assert.Equal(75.0, db.GetCar("5YJSA1E24FF100456")!.OriginalKWh);
        Assert.Equal("75", db.GetCar("5YJ3E1EB8JF100123")!.PackKey);
        Assert.Equal(0, LegacyImport.ImportAlertHistory(db, jsonFolder)); // already imported
    }

    [Fact]
    public async Task Builds_a_check_record_from_a_simulated_session()
    {
        var profile = new AutoDetectProfile();
        var monitor = new BatteryMonitor(profile);
        foreach (var frame in await AdapterTests.Take(new SimulatorAdapter(SimulatedCar.Model3, TimeSpan.Zero).StreamAsync(() => profile.MonitoredIds, _ => { }, CancellationToken.None), count: 2000))
            monitor.Process(frame);
        var data = monitor.Snapshot();
        var health = HealthCalculator.Evaluate(data);
        var buyer = BuyerCheck.Evaluate(new CheckInput(data, health, [], [], new AlertCoverage(true, 42, 42), T0));

        var check = CheckRecord.Create(data, health, buyer, [], [], new AlertCoverage(true, 42, 42), "Simulator: Model 3", T0, T0.AddMinutes(5), miles: false);

        Assert.True(CheckRecord.IsWorthSaving(data));
        Assert.Equal("Model 3", check.Model);
        Assert.Equal(2018, check.ModelYear);
        Assert.Equal(88.9, check.StateOfHealthPercent!.Value, 1);
        Assert.Contains("Buyer check", check.ReportHtml, StringComparison.Ordinal);
        Assert.StartsWith("Tesla Model 3 2018 battery health", check.SummaryText, StringComparison.Ordinal);
    }

    public void Dispose()
    {
        if (Directory.Exists(_folder))
            Directory.Delete(_folder, recursive: true);
    }

    private static CheckRecord Check(string? vin, DateTimeOffset ended, double? soh) => new()
    {
        Vin = vin,
        Model = vin is null ? null : "Model 3",
        ModelYear = vin is null ? null : 2018,
        Started = ended.AddMinutes(-10),
        Ended = ended,
        Source = "Simulator: Model 3",
        StateOfHealthPercent = soh,
        ReportHtml = "<html>report</html>",
    };
}

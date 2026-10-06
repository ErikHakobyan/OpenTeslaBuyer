using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using OpenTeslaBuyer.Core.Alerts;
using OpenTeslaBuyer.Core.Battery;

namespace OpenTeslaBuyer.Core.Storage;

/// <summary>
/// The tool's local SQLite database: cars, saved checks, alert episodes, parked cell snapshots and the recordings library.
/// One file, no server, works on every platform .NET runs on. Large files (recordings, exported reports)
/// stay on disk; the database keeps their paths.
/// </summary>
public sealed class AppDatabase : IDisposable
{
    public const string FileName = "data.db";

    private const int SchemaVersion = 2;

    /// <summary>Parked snapshots kept per car; older ones are dropped.</summary>
    internal const int SnapshotsPerCar = 30;

    private readonly SqliteConnection _connection;

    public AppDatabase(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        Migrate();
    }

    public string Path { get; }

    // ---------------------------------------------------------------- cars

    /// <summary>Records that a car was seen now; creates it on first sight.</summary>
    public void TouchCar(string vin, string? model, int? modelYear, DateTimeOffset seen) =>
        Execute("""
            INSERT INTO cars (vin, model, model_year, first_seen, last_seen) VALUES ($vin, $model, $year, $seen, $seen)
            ON CONFLICT(vin) DO UPDATE SET last_seen = $seen, model = COALESCE($model, model), model_year = COALESCE($year, model_year)
            """,
            ("$vin", vin), ("$model", model), ("$year", modelYear), ("$seen", Text(seen)));

    public CarRecord? GetCar(string vin) =>
        Query("SELECT * FROM cars WHERE vin = $vin", ReadCar, ("$vin", vin)).FirstOrDefault();

    /// <summary>Remembers the pack chosen and/or capacity typed for a car (null clears it).</summary>
    public void SetCarChoice(string vin, string? packKey, double? originalKWh)
    {
        TouchCarIfMissing(vin);
        Execute("UPDATE cars SET pack_key = $pack, original_kwh = $kwh WHERE vin = $vin",
            ("$vin", vin), ("$pack", packKey), ("$kwh", originalKWh));
    }

    public void SetCarNotes(string vin, string? notes)
    {
        TouchCarIfMissing(vin);
        Execute("UPDATE cars SET notes = $notes WHERE vin = $vin", ("$vin", vin), ("$notes", string.IsNullOrWhiteSpace(notes) ? null : notes));
    }

    /// <summary>All cars, most recently seen first, each with its check count and latest check.</summary>
    public IReadOnlyList<CarSummary> ListCars()
    {
        var cars = Query("SELECT * FROM cars ORDER BY last_seen DESC", ReadCar);
        var counts = Query("SELECT vin, COUNT(*) AS n FROM checks WHERE vin IS NOT NULL GROUP BY vin",
            r => (Vin: r.GetString(0), Count: r.GetInt32(1))).ToDictionary(c => c.Vin, c => c.Count);
        var latest = Query($"""
            SELECT {CheckColumns} FROM checks c
            WHERE c.vin IS NOT NULL AND c.id = (SELECT id FROM checks WHERE vin = c.vin ORDER BY ended DESC LIMIT 1)
            """, r => ReadCheck(r, withReport: false)).ToDictionary(c => c.Vin!);
        return cars.Select(c => new CarSummary(c, counts.GetValueOrDefault(c.Vin), latest.GetValueOrDefault(c.Vin))).ToList();
    }

    /// <summary>Removes a car with its checks and alert history. Recording files are left alone.</summary>
    public void DeleteCar(string vin)
    {
        using var transaction = _connection.BeginTransaction();
        foreach (var table in new[] { "checks", "alert_episodes", "cell_snapshots", "cars" })
            Execute($"DELETE FROM {table} WHERE vin = $vin", transaction, ("$vin", vin));
        transaction.Commit();
    }

    // ---------------------------------------------------------------- checks

    private const string CheckColumns =
        "id, vin, model, model_year, started, ended, source, platform, odometer_km, soh, original_kwh, original_source, " +
        "current_kwh, usable_kwh, cell_spread_mv, soc, active_alerts, buyer_summary, summary_text, isolation_kohm, pack_resistance_mohm, tests_json";

    public long AddCheck(CheckRecord check)
    {
        if (check.Vin is { } vin)
            TouchCar(vin, check.Model, check.ModelYear, check.Ended);

        Execute("""
            INSERT INTO checks (vin, model, model_year, started, ended, source, platform, odometer_km, soh, original_kwh, original_source,
                                current_kwh, usable_kwh, cell_spread_mv, soc, active_alerts, buyer_summary, summary_text, report_html,
                                isolation_kohm, pack_resistance_mohm, tests_json)
            VALUES ($vin, $model, $year, $started, $ended, $source, $platform, $odometer, $soh, $original, $originalSource,
                    $current, $usable, $spread, $soc, $alerts, $buyer, $summary, $html, $isolation, $resistance, $tests)
            """,
            ("$vin", check.Vin), ("$model", check.Model), ("$year", check.ModelYear), ("$started", Text(check.Started)),
            ("$ended", Text(check.Ended)), ("$source", check.Source), ("$platform", check.Platform), ("$odometer", check.OdometerKm),
            ("$soh", check.StateOfHealthPercent), ("$original", check.OriginalKWh), ("$originalSource", check.OriginalSource),
            ("$current", check.CurrentKWh), ("$usable", check.UsableKWh), ("$spread", check.CellSpreadMv), ("$soc", check.SocPercent),
            ("$alerts", check.ActiveAlerts), ("$buyer", check.BuyerSummary), ("$summary", check.SummaryText), ("$html", check.ReportHtml),
            ("$isolation", check.IsolationKOhm), ("$resistance", check.PackResistanceMilliOhm),
            ("$tests", check.Tests is { } tests ? JsonSerializer.Serialize(tests, StorageJsonContext.Default.TestSummary) : null));
        return Scalar<long>("SELECT last_insert_rowid()");
    }

    /// <summary>Saved checks, newest first; for one car when <paramref name="vin"/> is given. Reports are not loaded.</summary>
    public IReadOnlyList<CheckRecord> ListChecks(string? vin = null) => vin is null
        ? Query($"SELECT {CheckColumns} FROM checks ORDER BY ended DESC", r => ReadCheck(r, withReport: false))
        : Query($"SELECT {CheckColumns} FROM checks WHERE vin = $vin ORDER BY ended DESC", r => ReadCheck(r, withReport: false), ("$vin", vin));

    public string? GetCheckReport(long id) =>
        Query("SELECT report_html FROM checks WHERE id = $id", r => r.IsDBNull(0) ? null : r.GetString(0), ("$id", id)).FirstOrDefault();

    public void DeleteCheck(long id) => Execute("DELETE FROM checks WHERE id = $id", ("$id", id));

    // ---------------------------------------------------------------- alert episodes

    public List<AlertEpisode> LoadEpisodes(string vin) =>
        Query("SELECT code, start_at, end_at, ended_with_session FROM alert_episodes WHERE vin = $vin ORDER BY start_at", r => new AlertEpisode
        {
            Code = r.GetString(0),
            Start = Date(r.GetString(1)),
            End = r.IsDBNull(2) ? null : Date(r.GetString(2)),
            EndedWithSession = r.GetInt64(3) != 0,
        }, ("$vin", vin));

    /// <summary>Replaces a car's alert episodes.</summary>
    public void SaveEpisodes(string vin, IEnumerable<AlertEpisode> episodes)
    {
        using var transaction = _connection.BeginTransaction();
        Execute("DELETE FROM alert_episodes WHERE vin = $vin", transaction, ("$vin", vin));
        foreach (var episode in episodes)
        {
            Execute("INSERT INTO alert_episodes (vin, code, start_at, end_at, ended_with_session) VALUES ($vin, $code, $start, $end, $session)",
                transaction, ("$vin", vin), ("$code", episode.Code), ("$start", Text(episode.Start)),
                ("$end", episode.End is { } end ? Text(end) : null), ("$session", episode.EndedWithSession ? 1 : 0));
        }

        transaction.Commit();
    }

    // ---------------------------------------------------------------- parked cell snapshots

    /// <summary>Keeps a parked snapshot for the overnight test (once per time stamp); older ones beyond the limit are dropped.</summary>
    public void SaveSnapshot(string vin, CellSnapshot snapshot)
    {
        var takenAt = Text(snapshot.TakenAt.ToUniversalTime());
        if (Query("SELECT 1 FROM cell_snapshots WHERE vin = $vin AND taken_at = $at", _ => 1, ("$vin", vin), ("$at", takenAt)).Count > 0)
            return;

        TouchCarIfMissing(vin);
        using var transaction = _connection.BeginTransaction();
        Execute("INSERT INTO cell_snapshots (vin, taken_at, soc, temp_min, temp_max, volts_json) VALUES ($vin, $at, $soc, $min, $max, $volts)",
            transaction, ("$vin", vin), ("$at", takenAt), ("$soc", snapshot.SocPercent), ("$min", snapshot.TempMinC), ("$max", snapshot.TempMaxC),
            ("$volts", JsonSerializer.Serialize(snapshot.Volts.ToDictionary(), StorageJsonContext.Default.DictionaryInt32Double)));
        Execute("DELETE FROM cell_snapshots WHERE vin = $vin AND id NOT IN "
                + $"(SELECT id FROM cell_snapshots WHERE vin = $vin ORDER BY taken_at DESC LIMIT {SnapshotsPerCar})", transaction, ("$vin", vin));
        transaction.Commit();
    }

    /// <summary>A car's parked snapshots, newest first.</summary>
    public IReadOnlyList<CellSnapshot> ListSnapshots(string vin) =>
        Query("SELECT taken_at, soc, temp_min, temp_max, volts_json FROM cell_snapshots WHERE vin = $vin ORDER BY taken_at DESC", r => new CellSnapshot(
            Date(r.GetString(0)),
            JsonSerializer.Deserialize(r.GetString(4), StorageJsonContext.Default.DictionaryInt32Double) ?? [],
            r.IsDBNull(1) ? null : r.GetDouble(1),
            r.IsDBNull(2) ? null : r.GetDouble(2),
            r.IsDBNull(3) ? null : r.GetDouble(3)), ("$vin", vin));

    /// <summary>The newest snapshot of the car that is far enough before <paramref name="later"/> to compare with it.</summary>
    public CellSnapshot? SnapshotBefore(string vin, DateTimeOffset later) =>
        ListSnapshots(vin).FirstOrDefault(s => later - s.TakenAt >= CellDrift.MinimumGap && later - s.TakenAt <= CellDrift.MaximumGap);

    // ---------------------------------------------------------------- recordings

    /// <summary>Adds or updates a recording; null fields keep their stored value (except notes, which are replaced).</summary>
    public void UpsertRecording(RecordingRecord recording) =>
        Execute("""
            INSERT INTO recordings (path, vin, started, ended, frames, size_bytes, source, notes)
            VALUES ($path, $vin, $started, $ended, $frames, $size, $source, $notes)
            ON CONFLICT(path) DO UPDATE SET vin = COALESCE($vin, vin), ended = COALESCE($ended, ended), frames = COALESCE($frames, frames),
                size_bytes = COALESCE($size, size_bytes), source = COALESCE($source, source), notes = $notes
            """,
            ("$path", recording.Path), ("$vin", recording.Vin), ("$started", Text(recording.Started)),
            ("$ended", recording.Ended is { } e ? Text(e) : null), ("$frames", recording.Frames), ("$size", recording.SizeBytes),
            ("$source", recording.Source), ("$notes", recording.Notes));

    public IReadOnlyList<RecordingRecord> ListRecordings(string? vin = null) => vin is null
        ? Query("SELECT * FROM recordings ORDER BY started DESC", ReadRecording)
        : Query("SELECT * FROM recordings WHERE vin = $vin ORDER BY started DESC", ReadRecording, ("$vin", vin));

    public void DeleteRecording(string path) => Execute("DELETE FROM recordings WHERE path = $path", ("$path", path));

    /// <summary>Writes a consistent copy of the database to <paramref name="path"/> (which must not exist yet).</summary>
    public void BackupTo(string path) => Execute("VACUUM INTO $path", ("$path", path));

    /// <summary>Counts for the settings page.</summary>
    public (int Cars, int Checks, int Recordings) Counts() =>
        (Scalar<int>("SELECT COUNT(*) FROM cars"), Scalar<int>("SELECT COUNT(*) FROM checks"), Scalar<int>("SELECT COUNT(*) FROM recordings"));

    public void Dispose() => _connection.Dispose();

    // ---------------------------------------------------------------- plumbing

    private void Migrate()
    {
        var version = Scalar<long>("PRAGMA user_version");
        if (version >= SchemaVersion)
            return;

        using var transaction = _connection.BeginTransaction();
        if (version < 1)
            CreateVersion1(transaction);

        if (version < 2)
        {
            Execute("""
                ALTER TABLE checks ADD COLUMN isolation_kohm REAL;
                ALTER TABLE checks ADD COLUMN pack_resistance_mohm REAL;
                ALTER TABLE checks ADD COLUMN tests_json TEXT;
                CREATE TABLE IF NOT EXISTS cell_snapshots (
                    id INTEGER PRIMARY KEY AUTOINCREMENT, vin TEXT NOT NULL, taken_at TEXT NOT NULL, soc REAL, temp_min REAL, temp_max REAL,
                    volts_json TEXT NOT NULL);
                CREATE INDEX IF NOT EXISTS ix_cell_snapshots_vin ON cell_snapshots (vin, taken_at);
                """, transaction);
        }

        Execute($"PRAGMA user_version = {SchemaVersion}", transaction);
        transaction.Commit();
    }

    private void CreateVersion1(SqliteTransaction transaction)
    {
        Execute("""
            CREATE TABLE IF NOT EXISTS cars (
                vin TEXT PRIMARY KEY, model TEXT, model_year INTEGER, first_seen TEXT NOT NULL, last_seen TEXT NOT NULL,
                pack_key TEXT, original_kwh REAL, notes TEXT);
            CREATE TABLE IF NOT EXISTS checks (
                id INTEGER PRIMARY KEY AUTOINCREMENT, vin TEXT, model TEXT, model_year INTEGER, started TEXT NOT NULL, ended TEXT NOT NULL,
                source TEXT NOT NULL, platform TEXT, odometer_km REAL, soh REAL, original_kwh REAL, original_source TEXT,
                current_kwh REAL, usable_kwh REAL, cell_spread_mv REAL, soc REAL, active_alerts INTEGER NOT NULL DEFAULT 0,
                buyer_summary TEXT, summary_text TEXT, report_html TEXT);
            CREATE INDEX IF NOT EXISTS ix_checks_vin ON checks (vin, ended);
            CREATE TABLE IF NOT EXISTS alert_episodes (
                id INTEGER PRIMARY KEY AUTOINCREMENT, vin TEXT NOT NULL, code TEXT NOT NULL, start_at TEXT NOT NULL, end_at TEXT,
                ended_with_session INTEGER NOT NULL DEFAULT 0);
            CREATE INDEX IF NOT EXISTS ix_alert_episodes_vin ON alert_episodes (vin);
            CREATE TABLE IF NOT EXISTS recordings (
                path TEXT PRIMARY KEY, vin TEXT, started TEXT NOT NULL, ended TEXT, frames INTEGER, size_bytes INTEGER, source TEXT, notes TEXT);
            """, transaction);
    }

    private void TouchCarIfMissing(string vin)
    {
        if (GetCar(vin) is null)
            TouchCar(vin, null, null, DateTimeOffset.UtcNow);
    }

    private void Execute(string sql, params (string Name, object? Value)[] parameters) => Execute(sql, null, parameters);

    private void Execute(string sql, SqliteTransaction? transaction, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, transaction, parameters);
        command.ExecuteNonQuery();
    }

    private T Scalar<T>(string sql)
    {
        using var command = Command(sql, null, []);
        return (T)Convert.ChangeType(command.ExecuteScalar()!, typeof(T), CultureInfo.InvariantCulture);
    }

    private List<T> Query<T>(string sql, Func<SqliteDataReader, T> read, params (string Name, object? Value)[] parameters)
    {
        using var command = Command(sql, null, parameters);
        using var reader = command.ExecuteReader();
        var rows = new List<T>();
        while (reader.Read())
            rows.Add(read(reader));
        return rows;
    }

    private SqliteCommand Command(string sql, SqliteTransaction? transaction, (string Name, object? Value)[] parameters)
    {
        var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.Transaction = transaction;
        foreach (var (name, value) in parameters)
            command.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return command;
    }

    private static CarRecord ReadCar(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("vin")),
        NullableString(r, "model"),
        r.IsDBNull(r.GetOrdinal("model_year")) ? null : r.GetInt32(r.GetOrdinal("model_year")),
        Date(r.GetString(r.GetOrdinal("first_seen"))),
        Date(r.GetString(r.GetOrdinal("last_seen"))),
        NullableString(r, "pack_key"),
        NullableDouble(r, "original_kwh"),
        NullableString(r, "notes"));

    private static CheckRecord ReadCheck(SqliteDataReader r, bool withReport) => new()
    {
        Id = r.GetInt64(r.GetOrdinal("id")),
        Vin = NullableString(r, "vin"),
        Model = NullableString(r, "model"),
        ModelYear = r.IsDBNull(r.GetOrdinal("model_year")) ? null : r.GetInt32(r.GetOrdinal("model_year")),
        Started = Date(r.GetString(r.GetOrdinal("started"))),
        Ended = Date(r.GetString(r.GetOrdinal("ended"))),
        Source = r.GetString(r.GetOrdinal("source")),
        Platform = NullableString(r, "platform"),
        OdometerKm = NullableDouble(r, "odometer_km"),
        StateOfHealthPercent = NullableDouble(r, "soh"),
        OriginalKWh = NullableDouble(r, "original_kwh"),
        OriginalSource = NullableString(r, "original_source"),
        CurrentKWh = NullableDouble(r, "current_kwh"),
        UsableKWh = NullableDouble(r, "usable_kwh"),
        CellSpreadMv = NullableDouble(r, "cell_spread_mv"),
        SocPercent = NullableDouble(r, "soc"),
        ActiveAlerts = r.GetInt32(r.GetOrdinal("active_alerts")),
        BuyerSummary = NullableString(r, "buyer_summary"),
        SummaryText = NullableString(r, "summary_text"),
        IsolationKOhm = NullableDouble(r, "isolation_kohm"),
        PackResistanceMilliOhm = NullableDouble(r, "pack_resistance_mohm"),
        Tests = NullableString(r, "tests_json") is { } json ? JsonSerializer.Deserialize(json, StorageJsonContext.Default.TestSummary) : null,
        ReportHtml = withReport ? NullableString(r, "report_html") : null,
    };

    private static RecordingRecord ReadRecording(SqliteDataReader r) => new(
        r.GetString(r.GetOrdinal("path")),
        NullableString(r, "vin"),
        Date(r.GetString(r.GetOrdinal("started"))),
        r.IsDBNull(r.GetOrdinal("ended")) ? null : Date(r.GetString(r.GetOrdinal("ended"))),
        r.IsDBNull(r.GetOrdinal("frames")) ? null : r.GetInt64(r.GetOrdinal("frames")),
        r.IsDBNull(r.GetOrdinal("size_bytes")) ? null : r.GetInt64(r.GetOrdinal("size_bytes")),
        NullableString(r, "source"),
        NullableString(r, "notes"));

    private static string? NullableString(SqliteDataReader r, string column) =>
        r.IsDBNull(r.GetOrdinal(column)) ? null : r.GetString(r.GetOrdinal(column));

    private static double? NullableDouble(SqliteDataReader r, string column) =>
        r.IsDBNull(r.GetOrdinal(column)) ? null : r.GetDouble(r.GetOrdinal(column));

    private static string Text(DateTimeOffset value) => value.ToString("o", CultureInfo.InvariantCulture);

    private static DateTimeOffset Date(string value) => DateTimeOffset.Parse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>Alert history kept in the database.</summary>
public sealed class DatabaseAlertHistoryStore(AppDatabase database) : IAlertHistoryStore
{
    public List<AlertEpisode> Load(string vin) => database.LoadEpisodes(vin);

    public void Save(string vin, IEnumerable<AlertEpisode> episodes) =>
        database.SaveEpisodes(vin, episodes.OrderBy(e => e.Start).TakeLast(AlertHistoryStore.MaxEpisodesPerVin));
}

/// <summary>Moves data kept by earlier versions (JSON files and per-VIN settings) into the database.</summary>
public static class LegacyImport
{
    /// <summary>Imports <c>&lt;VIN&gt;.alerts.json</c> files; returns how many cars were imported. Files are left in place.</summary>
    public static int ImportAlertHistory(AppDatabase database, string folder)
    {
        if (!Directory.Exists(folder))
            return 0;

        var store = new AlertHistoryStore(folder);
        var imported = 0;
        foreach (var file in Directory.GetFiles(folder, "*.alerts.json"))
        {
            var vin = System.IO.Path.GetFileName(file)[..^".alerts.json".Length];
            var episodes = store.Load(vin);
            if (episodes.Count == 0 || database.LoadEpisodes(vin).Count > 0)
                continue;

            database.TouchCar(vin, null, null, episodes.Max(e => e.End ?? e.Start));
            database.SaveEpisodes(vin, episodes);
            imported++;
        }

        return imported;
    }

    public static void ImportCarChoices(AppDatabase database, IReadOnlyDictionary<string, double> capacities, IReadOnlyDictionary<string, string> packs)
    {
        foreach (var vin in capacities.Keys.Union(packs.Keys))
        {
            if (database.GetCar(vin) is { PackKey: null, OriginalKWh: null } or null)
                database.SetCarChoice(vin, packs.GetValueOrDefault(vin), capacities.TryGetValue(vin, out var kwh) ? kwh : null);
        }
    }
}

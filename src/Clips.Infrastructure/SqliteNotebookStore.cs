using System.Globalization;
using System.IO;
using Clips.Core;
using Microsoft.Data.Sqlite;

namespace Clips.Infrastructure;

// A single serialized connection keeps reads and writes consistent. All SQLite work runs off the UI thread.
public sealed class SqliteNotebookStore : INotebookStore, IDisposable
{
    private readonly SqliteConnection connection;
    private readonly SemaphoreSlim gate = new(1, 1);
    public SqliteNotebookStore(AppPaths paths) => connection = new(new SqliteConnectionStringBuilder { DataSource = paths.Database, ForeignKeys = true, Pooling = false }.ToString());
    private async Task<T> Run<T>(Func<T> work)
    {
        await gate.WaitAsync();
        try { return await Task.Run(work); }
        finally { gate.Release(); }
    }
    private async Task Run(Action work) => await Run(() => { work(); return true; });
    private SqliteCommand Command(string sql, SqliteTransaction? tx = null, params (string Key, object? Value)[] values)
    {
        var cmd = connection.CreateCommand(); cmd.CommandText = sql; cmd.Transaction = tx;
        foreach (var (key, value) in values) cmd.Parameters.AddWithValue(key, value ?? DBNull.Value);
        return cmd;
    }
    private int Exec(string sql, SqliteTransaction? tx = null, params (string Key, object? Value)[] values)
    { using var cmd = Command(sql, tx, values); return cmd.ExecuteNonQuery(); }
    private object? Scalar(string sql, SqliteTransaction? tx = null, params (string Key, object? Value)[] values)
    { using var cmd = Command(sql, tx, values); return cmd.ExecuteScalar(); }
    private static string Id(Guid id) => id.ToString("D");
    private static string Time(DateTime time) => time.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static DateTime Date(SqliteDataReader r, int index) => DateTime.Parse(r.GetString(index), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
    private static string? Text(SqliteDataReader r, int index) => r.IsDBNull(index) ? null : r.GetString(index);
    public Task InitializeAsync() => Run(() =>
    {
        connection.Open();
        Exec("PRAGMA journal_mode=WAL; PRAGMA synchronous=FULL; PRAGMA busy_timeout=5000;");
        var version = Convert.ToInt32(Scalar("PRAGMA user_version;"), CultureInfo.InvariantCulture);
        if (version > 1) throw new InvalidOperationException("This data was created by a newer application version.");
        if (version == 0)
        {
            using var stream = typeof(SqliteNotebookStore).Assembly.GetManifestResourceStream("Clips.Infrastructure.Migrations.001_initial.sql")!;
            using var reader = new StreamReader(stream);
            using var tx = connection.BeginTransaction(); Exec(reader.ReadToEnd(), tx); tx.Commit();
        }
    });
    public Task<IReadOnlyList<Notebook>> GetNotebooksAsync(bool archived = false) => Run<IReadOnlyList<Notebook>>(() =>
    {
        using var cmd = Command("SELECT * FROM Notebook WHERE IsArchived=$a ORDER BY UpdatedAtUtc DESC,Id", null, ("$a", archived ? 1 : 0));
        using var r = cmd.ExecuteReader(); var list = new List<Notebook>();
        while (r.Read()) list.Add(new(Guid.Parse(r.GetString(0)), r.GetString(1), r.GetString(2), Text(r, 3), Date(r, 4), Date(r, 5), r.GetBoolean(6)));
        return list;
    });
    public Task<Notebook> SaveNotebookAsync(Notebook n) => Run(() =>
    {
        n = n with { Name = Rules.NotebookName(n.Name), CoverColor = Rules.CoverColor(n.CoverColor), Icon = Rules.Icon(n.Icon) };
        if (n.IsArchived && Scalar("SELECT Id FROM CaptureSession WHERE NotebookId=$n AND Status IN (0,1)", null, ("$n", Id(n.Id))) != null)
            throw new InvalidOperationException("End this notebook’s session before archiving it.");
        Exec("INSERT INTO Notebook VALUES($id,$name,$color,$icon,$created,$updated,$archived) ON CONFLICT(Id) DO UPDATE SET Name=$name,CoverColor=$color,Icon=$icon,UpdatedAtUtc=$updated,IsArchived=$archived", null,
            ("$id", Id(n.Id)), ("$name", n.Name), ("$color", n.CoverColor), ("$icon", n.Icon), ("$created", Time(n.CreatedAtUtc)), ("$updated", Time(n.UpdatedAtUtc)), ("$archived", n.IsArchived ? 1 : 0));
        return n;
    });
    public Task DeleteNotebookAsync(Guid id) => Run(() =>
    {
        if (Scalar("SELECT Id FROM CaptureSession WHERE NotebookId=$n AND Status IN (0,1)", null, ("$n", Id(id))) != null)
            throw new InvalidOperationException("End this notebook’s session before deleting it.");
        Exec("DELETE FROM Notebook WHERE Id=$id", null, ("$id", Id(id)));
    });
    private CaptureSession? OpenSession(SqliteTransaction? tx = null)
    {
        using var cmd = Command("SELECT * FROM CaptureSession WHERE Status IN (0,1) ORDER BY StartedAtUtc DESC LIMIT 1", tx);
        using var r = cmd.ExecuteReader();
        return r.Read() ? new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Date(r, 2), r.IsDBNull(3) ? null : Date(r, 3), (SessionStatus)r.GetInt32(4), Date(r, 5)) : null;
    }
    public Task<CaptureSession?> GetOpenSessionAsync() => Run(() => OpenSession());
    public Task<CaptureSession> StartSessionAsync(Guid notebookId, bool endExisting = false) => Run(() =>
    {
        using var tx = connection.BeginTransaction();
        var old = OpenSession(tx);
        if (old != null && !endExisting) throw new SessionConflictException(old.NotebookId);
        if (Scalar("SELECT Id FROM Notebook WHERE Id=$id AND IsArchived=0", tx, ("$id", Id(notebookId))) == null)
            throw new InvalidOperationException("Choose an available notebook.");
        var now = DateTime.UtcNow;
        if (old != null) Exec("UPDATE CaptureSession SET Status=2,EndedAtUtc=$t WHERE Id=$id", tx, ("$t", Time(now)), ("$id", Id(old.Id)));
        var session = new CaptureSession(Guid.NewGuid(), notebookId, now, null, SessionStatus.Active, now);
        Exec("INSERT INTO CaptureSession VALUES($id,$n,$t,NULL,0,$t)", tx, ("$id", Id(session.Id)), ("$n", Id(notebookId)), ("$t", Time(now)));
        tx.Commit(); return session;
    });
    public Task<CaptureSession> TransitionSessionAsync(Guid sessionId, SessionStatus status) => Run(() =>
    {
        if (!Enum.IsDefined(status)) throw new ArgumentException("Invalid session state.");
        using var tx = connection.BeginTransaction(); var session = OpenSession(tx);
        if (session == null || session.Id != sessionId) throw new InvalidOperationException("This session has already ended.");
        var ended = status == SessionStatus.Ended ? DateTime.UtcNow : (DateTime?)null;
        Exec("UPDATE CaptureSession SET Status=$s,EndedAtUtc=$t WHERE Id=$id", tx, ("$s", (int)status), ("$t", ended.HasValue ? Time(ended.Value) : null), ("$id", Id(sessionId)));
        tx.Commit(); return session with { Status = status, EndedAtUtc = ended };
    });
    private static CaptureItem ReadCapture(SqliteDataReader r) => new(Guid.Parse(r.GetString(0)), Guid.Parse(r.GetString(1)), Guid.Parse(r.GetString(2)), (CaptureType)r.GetInt32(3), Text(r, 4), Text(r, 5), Text(r, 6), Text(r, 7), Text(r, 8), Text(r, 9), Text(r, 10), Text(r, 11), Date(r, 12), r.GetInt32(13), Date(r, 14), Date(r, 15));
    public Task<IReadOnlyList<CaptureItem>> GetCapturesAsync(Guid notebookId) => Run<IReadOnlyList<CaptureItem>>(() =>
    {
        using var cmd = Command("SELECT * FROM CaptureItem WHERE NotebookId=$n ORDER BY DisplayOrder,Id", null, ("$n", Id(notebookId)));
        using var r = cmd.ExecuteReader(); var list = new List<CaptureItem>(); while (r.Read()) list.Add(ReadCapture(r)); return list;
    });
    public Task<CaptureItem> AppendCaptureAsync(CaptureItem item) => Run(() =>
    {
        using var tx = connection.BeginTransaction(); var session = OpenSession(tx);
        if (session?.Status != SessionStatus.Active || session.Id != item.SessionId || session.NotebookId != item.NotebookId)
            throw new InvalidOperationException("Capture session changed or is paused. Try again after starting a session.");
        var order = Convert.ToInt32(Scalar("SELECT COALESCE(MAX(DisplayOrder),0)+1 FROM CaptureItem WHERE NotebookId=$n", tx, ("$n", Id(item.NotebookId))), CultureInfo.InvariantCulture);
        item = item with { DisplayOrder = order };
        Exec("INSERT INTO CaptureItem VALUES($id,$n,$s,$type,$text,$image,$note,$app,$window,NULL,NULL,NULL,$time,$order,$created,$updated)", tx,
            ("$id", Id(item.Id)), ("$n", Id(item.NotebookId)), ("$s", Id(item.SessionId)), ("$type", (int)item.Type), ("$text", item.ContentText), ("$image", item.ImageRelativePath),
            ("$note", Rules.Note(item.UserNote)), ("$app", item.SourceAppName), ("$window", item.SourceWindowTitle), ("$time", Time(item.CapturedAtUtc)), ("$order", order), ("$created", Time(item.CreatedAtUtc)), ("$updated", Time(item.UpdatedAtUtc)));
        Exec("UPDATE Notebook SET UpdatedAtUtc=$t WHERE Id=$n", tx, ("$t", Time(item.UpdatedAtUtc)), ("$n", Id(item.NotebookId)));
        tx.Commit(); return item;
    });
    public Task SaveNoteAsync(Guid id, string? note) => Run(() =>
    {
        note = Rules.Note(note); using var tx = connection.BeginTransaction(); var now = Time(DateTime.UtcNow);
        Exec("UPDATE Notebook SET UpdatedAtUtc=$t WHERE Id=(SELECT NotebookId FROM CaptureItem WHERE Id=$id)", tx, ("$t", now), ("$id", Id(id)));
        if (Exec("UPDATE CaptureItem SET UserNote=$note,UpdatedAtUtc=$t WHERE Id=$id", tx, ("$note", note), ("$t", now), ("$id", Id(id))) == 0)
            throw new InvalidOperationException("This capture no longer exists.");
        tx.Commit();
    });
    private List<Guid> OrderedIds(string notebook, SqliteTransaction tx)
    {
        using var cmd = Command("SELECT Id FROM CaptureItem WHERE NotebookId=$n ORDER BY DisplayOrder,Id", tx, ("$n", notebook));
        using var r = cmd.ExecuteReader(); var ids = new List<Guid>(); while (r.Read()) ids.Add(Guid.Parse(r.GetString(0))); return ids;
    }
    private void Reindex(string notebook, List<Guid> ids, SqliteTransaction tx)
    {
        var now = Time(DateTime.UtcNow);
        for (var i = 0; i < ids.Count; i++) Exec("UPDATE CaptureItem SET DisplayOrder=$o,UpdatedAtUtc=$t WHERE Id=$id", tx, ("$o", i + 1), ("$t", now), ("$id", Id(ids[i])));
        Exec("UPDATE Notebook SET UpdatedAtUtc=$t WHERE Id=$id", tx, ("$t", now), ("$id", notebook));
    }
    public Task MoveCaptureAsync(Guid id, int direction) => Run(() =>
    {
        if (direction is not (-1 or 1)) throw new ArgumentException("Move by one position.");
        using var tx = connection.BeginTransaction(); var notebook = Scalar("SELECT NotebookId FROM CaptureItem WHERE Id=$id", tx, ("$id", Id(id))) as string;
        if (notebook == null) return;
        var ids = OrderedIds(notebook, tx); var index = ids.IndexOf(id); var target = index + direction;
        if (target >= 0 && target < ids.Count) { (ids[index], ids[target]) = (ids[target], ids[index]); Reindex(notebook, ids, tx); }
        tx.Commit();
    });
    public Task DeleteCaptureAsync(Guid id) => Run(() =>
    {
        using var tx = connection.BeginTransaction(); var notebook = Scalar("SELECT NotebookId FROM CaptureItem WHERE Id=$id", tx, ("$id", Id(id))) as string;
        Exec("DELETE FROM CaptureItem WHERE Id=$id", tx, ("$id", Id(id)));
        if (notebook != null) Reindex(notebook, OrderedIds(notebook, tx), tx); tx.Commit();
    });
    public Task<AppSettings> GetSettingsAsync() => Run(() =>
    {
        using var cmd = Command("SELECT * FROM AppSettings WHERE Id=1"); using var r = cmd.ExecuteReader(); r.Read();
        return new AppSettings(r.GetString(1), r.GetString(2), r.GetString(3), r.GetBoolean(4), r.GetBoolean(5), (AppTheme)r.GetInt32(6));
    });
    public Task SaveSettingsAsync(AppSettings s) => Run(() =>
        Exec("UPDATE AppSettings SET TextCaptureShortcut=$text,ImageCaptureShortcut=$image,OpenTrayShortcut=$tray,NotificationsEnabled=$notify,StartMinimizedToTray=$min,Theme=$theme WHERE Id=1", null,
            ("$text", s.TextCaptureShortcut), ("$image", s.ImageCaptureShortcut), ("$tray", s.OpenTrayShortcut), ("$notify", s.NotificationsEnabled ? 1 : 0), ("$min", s.StartMinimizedToTray ? 1 : 0), ("$theme", (int)s.Theme)));
    public void Dispose() { connection.Dispose(); gate.Dispose(); }
}

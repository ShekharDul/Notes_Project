using Clips.Core;
using Clips.Infrastructure;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Clips.Tests;

public sealed class TestDatabase : IAsyncDisposable
{
    public AppPaths Paths { get; } = new(Path.Combine(Path.GetTempPath(), "ClipsTests", Guid.NewGuid().ToString("D")));
    public SqliteNotebookStore Store { get; }
    public ImageStorage Images { get; }
    public NotebookService Notebooks { get; }
    public TestDatabase() { Store = new(Paths); Images = new(Paths); Notebooks = new(Store); }
    public async Task<Notebook> InitializeAsync()
    { await Store.InitializeAsync(); return await Notebooks.CreateAsync("Research", Product.CoverColors[0], "📚"); }
    public CaptureService Service(ISelectedTextReader? reader = null, INotebookStore? store = null) => new(store ?? Store, Images, reader ?? new FakeReader(), new NullLog());
    public ValueTask DisposeAsync() { Store.Dispose(); Directory.Delete(Paths.Root, true); return ValueTask.CompletedTask; }
}
public sealed class FakeReader : ISelectedTextReader
{
    public SelectionResult Result { get; set; } = new(SelectionStatus.Success, "Selected text", new("notepad", "Untitled - Notepad"));
    public int Calls { get; private set; }
    public Func<Task>? BeforeReturn { get; set; }
    public async Task<SelectionResult> ReadAsync() { Calls++; if (BeforeReturn != null) await BeforeReturn(); return Result; }
}
public sealed class NullLog : ILocalLog { public void Event(string eventName, int? errorCode = null) { } }
public sealed class NotebookAndCaptureTests
{
    // A real 1×1 PNG; region-size rejection belongs to the snipping UI.
    private static byte[] Png => Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mP8/x8AAwMCAO+jZ1kAAAAASUVORK5CYII=");
    [Theory]
    [InlineData("")][InlineData("  ")]
    public void RejectsBlankNames(string name) => Assert.Throws<ArgumentException>(() => Rules.NotebookName(name));
    [Fact] public void RejectsOverlongNames() => Assert.Throws<ArgumentException>(() => Rules.NotebookName(new string('x', 101)));
    [Theory][InlineData("red")][InlineData("#123")][InlineData("#12345Z")]
    public void RejectsInvalidCoverColors(string color) => Assert.Throws<ArgumentException>(() => Rules.CoverColor(color));
    [Fact] public void NormalizationPreservesParagraphBreaks()
    {
        Assert.Equal("First line\n\nSecond paragraph\n\n\nThird", Rules.NormalizeText("  First\t  line \r\n\r\n Second  paragraph\r\n\r\n\r\n Third  "));
    }
    [Fact] public void NotesHaveEnforcedLimit() => Assert.Throws<ArgumentException>(() => Rules.Note(new string('a', 10001)));
    [Fact] public async Task MigrationAndNotebookSurviveReopening()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); db.Store.Dispose();
        using var reopened = new SqliteNotebookStore(db.Paths); await reopened.InitializeAsync();
        var saved = Assert.Single(await reopened.GetNotebooksAsync()); Assert.Equal(n, saved);
        Assert.Equal(new AppSettings(), await reopened.GetSettingsAsync());
    }
    [Fact] public async Task OnlyOneOpenSessionIncludingPaused()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync();
        var other = await db.Notebooks.CreateAsync("Other", Product.CoverColors[1], null);
        var first = await db.Store.StartSessionAsync(n.Id); await db.Store.TransitionSessionAsync(first.Id, SessionStatus.Paused);
        await Assert.ThrowsAsync<SessionConflictException>(() => db.Store.StartSessionAsync(other.Id));
        Assert.Equal(first.Id, (await db.Store.GetOpenSessionAsync())!.Id);
        var next = await db.Store.StartSessionAsync(other.Id, true); Assert.Equal(next.Id, (await db.Store.GetOpenSessionAsync())!.Id);
    }
    [Fact] public async Task SessionTransitionsAndRecovery()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); var s = await db.Store.StartSessionAsync(n.Id);
        Assert.Equal(SessionStatus.Paused, (await db.Store.TransitionSessionAsync(s.Id, SessionStatus.Paused)).Status);
        Assert.Equal(SessionStatus.Active, (await db.Store.TransitionSessionAsync(s.Id, SessionStatus.Active)).Status);
        db.Store.Dispose(); using var reopened = new SqliteNotebookStore(db.Paths); await reopened.InitializeAsync();
        Assert.Equal(s.Id, (await reopened.GetOpenSessionAsync())!.Id);
        var ended = await reopened.TransitionSessionAsync(s.Id, SessionStatus.Ended); Assert.NotNull(ended.EndedAtUtc);
        Assert.Null(await reopened.GetOpenSessionAsync());
        await Assert.ThrowsAsync<InvalidOperationException>(() => reopened.TransitionSessionAsync(s.Id, SessionStatus.Active));
    }
    [Fact] public async Task NoSessionDoesNotReadSelection()
    {
        await using var db = new TestDatabase(); await db.InitializeAsync(); var reader = new FakeReader();
        var result = await db.Service(reader).CaptureTextAsync(); Assert.Null(result.Item); Assert.True(result.OpenApp); Assert.Equal(0, reader.Calls);
    }
    [Fact] public async Task PausedSessionDoesNotReadOrWrite()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); var s = await db.Store.StartSessionAsync(n.Id);
        await db.Store.TransitionSessionAsync(s.Id, SessionStatus.Paused); var reader = new FakeReader();
        var result = await db.Service(reader).CaptureTextAsync(); Assert.Equal("Capture is paused.", result.Message); Assert.Equal(0, reader.Calls);
        Assert.Null((await db.Service().CaptureImageAsync(s.Id, Png, new())).Item); Assert.Empty(Directory.GetFiles(db.Paths.Captures));
    }
    [Theory][InlineData(SelectionStatus.UnsupportedApplication, true)][InlineData(SelectionStatus.NoSelection, true)][InlineData(SelectionStatus.AccessDeniedOrSecureControl, false)]
    public async Task FallbackIsVisibleAndDoesNotSave(SelectionStatus status, bool screenshot)
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); await db.Store.StartSessionAsync(n.Id);
        var result = await db.Service(new FakeReader { Result = new(status) }).CaptureTextAsync();
        Assert.Null(result.Item); Assert.Equal(screenshot, result.OfferScreenshot); Assert.True(result.OpenApp); Assert.Empty(await db.Store.GetCapturesAsync(n.Id));
    }
    [Fact] public async Task CapturesAppendAndMovesPersist()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); await db.Store.StartSessionAsync(n.Id); var service = db.Service();
        var a = (await service.CaptureTextAsync()).Item!; var b = (await service.CaptureTextAsync()).Item!; var c = (await service.CaptureTextAsync()).Item!;
        Assert.Equal(new[] { 1, 2, 3 }, (await db.Store.GetCapturesAsync(n.Id)).Select(i => i.DisplayOrder));
        await db.Store.MoveCaptureAsync(c.Id, -1); await db.Store.MoveCaptureAsync(a.Id, 1);
        Assert.Equal(new[] { c.Id, a.Id, b.Id }, (await db.Store.GetCapturesAsync(n.Id)).Select(i => i.Id));
        db.Store.Dispose(); using var reopened = new SqliteNotebookStore(db.Paths); await reopened.InitializeAsync();
        Assert.Equal(new[] { c.Id, a.Id, b.Id }, (await reopened.GetCapturesAsync(n.Id)).Select(i => i.Id));
        await reopened.MoveCaptureAsync(c.Id, -1); Assert.Equal(c.Id, (await reopened.GetCapturesAsync(n.Id))[0].Id);
    }
    [Fact] public async Task UndoDeletesOnlyRequestedCaptureAndCompactsOrder()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); await db.Store.StartSessionAsync(n.Id); var service = db.Service();
        var first = (await service.CaptureTextAsync()).Item!; var second = (await service.CaptureTextAsync()).Item!;
        await service.UndoAsync(first); await service.UndoAsync(first);
        var remaining = Assert.Single(await db.Store.GetCapturesAsync(n.Id)); Assert.Equal(second.Id, remaining.Id); Assert.Equal(1, remaining.DisplayOrder);
    }
    [Fact] public async Task ImageUndoRemovesDatabaseAndFile()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); var s = await db.Store.StartSessionAsync(n.Id); var service = db.Service();
        var item = (await service.CaptureImageAsync(s.Id, Png, new("test", "source"))).Item!;
        Assert.True(File.Exists(db.Images.Resolve(item.ImageRelativePath!))); Assert.Equal("source", item.SourceWindowTitle);
        await service.UndoAsync(item); Assert.Empty(await db.Store.GetCapturesAsync(n.Id)); Assert.Empty(Directory.GetFiles(db.Paths.Captures));
    }
    [Fact] public async Task SessionSwitchWhileReadingCannotSaveIntoEitherNotebook()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); await db.Store.StartSessionAsync(n.Id);
        var other = await db.Notebooks.CreateAsync("Other", Product.CoverColors[1], null);
        var reader = new FakeReader { BeforeReturn = async () => { await db.Store.StartSessionAsync(other.Id, true); } };
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Service(reader).CaptureTextAsync());
        Assert.Empty(await db.Store.GetCapturesAsync(n.Id)); Assert.Empty(await db.Store.GetCapturesAsync(other.Id));
    }
    [Fact] public async Task ImageFailureCleansFinalFileAndRollsBackOrder()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); var s = await db.Store.StartSessionAsync(n.Id);
        // A database trigger injects failure after the image is safely written, inside the insert transaction.
        using (var connection = new SqliteConnection($"Data Source={db.Paths.Database};Pooling=False"))
        {
            await connection.OpenAsync(); using var cmd = connection.CreateCommand();
            cmd.CommandText = "CREATE TRIGGER FailCapture BEFORE INSERT ON CaptureItem BEGIN SELECT RAISE(ABORT,'test failure'); END;"; await cmd.ExecuteNonQueryAsync();
        }
        await Assert.ThrowsAsync<SqliteException>(() => db.Service().CaptureImageAsync(s.Id, Png, new()));
        Assert.Empty(await db.Store.GetCapturesAsync(n.Id)); Assert.Empty(Directory.GetFiles(db.Paths.Captures));
        using (var connection = new SqliteConnection($"Data Source={db.Paths.Database};Pooling=False"))
        {
            await connection.OpenAsync(); using var cmd = connection.CreateCommand(); cmd.CommandText = "DROP TRIGGER FailCapture"; await cmd.ExecuteNonQueryAsync();
        }
        Assert.Equal(1, (await db.Service().CaptureTextAsync()).Item!.DisplayOrder);
    }
    [Fact] public async Task ConcurrentCaptureRequestsProduceUniqueOrders()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); await db.Store.StartSessionAsync(n.Id);
        var outcomes = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => db.Service().CaptureTextAsync()));
        Assert.Equal(20, outcomes.Length); Assert.Equal(Enumerable.Range(1, 20), (await db.Store.GetCapturesAsync(n.Id)).Select(c => c.DisplayOrder));
    }
    [Fact] public async Task NotesAndSettingsPersist()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); await db.Store.StartSessionAsync(n.Id);
        var item = (await db.Service().CaptureTextAsync()).Item!; await db.Store.SaveNoteAsync(item.Id, "Personal note\nSecond line");
        var settings = new AppSettings("Ctrl+Alt+Q", "Ctrl+Alt+W", "Ctrl+Alt+E", false, true, AppTheme.Dark); await db.Store.SaveSettingsAsync(settings);
        db.Store.Dispose(); using var reopened = new SqliteNotebookStore(db.Paths); await reopened.InitializeAsync();
        Assert.Equal("Personal note\nSecond line", Assert.Single(await reopened.GetCapturesAsync(n.Id)).UserNote); Assert.Equal(settings, await reopened.GetSettingsAsync());
    }
    [Fact] public async Task StartupCleanupKeepsReferencedImages()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); var s = await db.Store.StartSessionAsync(n.Id);
        var saved = (await db.Service().CaptureImageAsync(s.Id, Png, new())).Item!;
        var orphan = await db.Images.SaveAsync(Guid.NewGuid(), Png); await File.WriteAllBytesAsync(Path.Combine(db.Paths.Captures, Guid.NewGuid() + ".png.tmp"), Png);
        await db.Images.ReconcileAsync(db.Store);
        Assert.False(File.Exists(db.Images.Resolve(orphan))); Assert.True(File.Exists(db.Images.Resolve(saved.ImageRelativePath!))); Assert.Single(Directory.GetFiles(db.Paths.Captures));
    }
    [Fact] public void ImagePathsCannotEscapeStorage() => Assert.Throws<ArgumentException>(() => new ImageStorage(new AppPaths(Path.Combine(Path.GetTempPath(), "ClipsPathTest"))).Resolve("../file.png"));
    [Fact] public void ShortcutConflictsAreValidated()
    {
        Assert.Throws<ArgumentException>(() => Hotkey.Validate(new("Ctrl+Alt+S", "Alt+Ctrl+S")));
        Assert.Throws<ArgumentException>(() => Hotkey.Parse("Ctrl+L")); Assert.Equal(3u, Hotkey.Parse("Ctrl+Alt+S").Modifiers);
    }
    [Fact] public async Task ArchiveAndDeleteRequireEndingSession()
    {
        await using var db = new TestDatabase(); var n = await db.InitializeAsync(); var s = await db.Store.StartSessionAsync(n.Id);
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.SaveNotebookAsync(n with { IsArchived = true }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => db.Store.DeleteNotebookAsync(n.Id));
        await db.Store.TransitionSessionAsync(s.Id, SessionStatus.Ended); await db.Store.SaveNotebookAsync(n with { IsArchived = true });
        Assert.Empty(await db.Store.GetNotebooksAsync()); Assert.Single(await db.Store.GetNotebooksAsync(true)); await db.Store.DeleteNotebookAsync(n.Id); Assert.Empty(await db.Store.GetNotebooksAsync(true));
    }
}

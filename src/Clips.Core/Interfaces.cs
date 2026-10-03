namespace Clips.Core;

public interface INotebookStore
{
    Task InitializeAsync();
    Task<IReadOnlyList<Notebook>> GetNotebooksAsync(bool archived = false);
    Task<Notebook> SaveNotebookAsync(Notebook notebook);
    Task DeleteNotebookAsync(Guid id);
    Task<CaptureSession?> GetOpenSessionAsync();
    Task<CaptureSession> StartSessionAsync(Guid notebookId, bool endExisting = false);
    Task<CaptureSession> TransitionSessionAsync(Guid sessionId, SessionStatus status);
    Task<IReadOnlyList<CaptureItem>> GetCapturesAsync(Guid notebookId);
    Task<CaptureItem> AppendCaptureAsync(CaptureItem item);
    Task SaveNoteAsync(Guid id, string? note);
    Task MoveCaptureAsync(Guid id, int direction);
    Task DeleteCaptureAsync(Guid id);
    Task<AppSettings> GetSettingsAsync();
    Task SaveSettingsAsync(AppSettings settings);
}
public interface IImageStorage
{
    Task<string> SaveAsync(Guid id, byte[] png);
    Task DeleteAsync(string relativePath);
    Task CleanupTemporaryAsync();
    string Resolve(string relativePath);
}
public interface ISelectedTextReader { Task<SelectionResult> ReadAsync(); }
public interface ILocalLog { void Event(string eventName, int? errorCode = null); }

public sealed class NotebookService(INotebookStore store)
{
    public Task<Notebook> CreateAsync(string name, string color, string? icon)
    {
        var now = DateTime.UtcNow;
        return store.SaveNotebookAsync(new(Guid.NewGuid(), Rules.NotebookName(name), Rules.CoverColor(color), Rules.Icon(icon), now, now));
    }
    public Task<Notebook> UpdateAsync(Notebook notebook, string name, string color, string? icon) => store.SaveNotebookAsync(notebook with
    { Name = Rules.NotebookName(name), CoverColor = Rules.CoverColor(color), Icon = Rules.Icon(icon), UpdatedAtUtc = DateTime.UtcNow });
}

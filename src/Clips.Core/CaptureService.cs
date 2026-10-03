namespace Clips.Core;

public sealed class CaptureService(INotebookStore store, IImageStorage images, ISelectedTextReader reader, ILocalLog log)
{
    private readonly SemaphoreSlim gate = new(1, 1);
    public async Task<CaptureOutcome> CaptureTextAsync()
    {
        await gate.WaitAsync();
        try
        {
            var session = await store.GetOpenSessionAsync();
            if (Unavailable(session) is { } unavailable) return unavailable;
            var result = await reader.ReadAsync();
            if (result.Status != SelectionStatus.Success) return Rules.SelectionFallback(result.Status);
            var text = Rules.NormalizeText(result.Text ?? "");
            if (text.Length == 0) return Rules.SelectionFallback(SelectionStatus.NoSelection);
            var item = NewItem(session!, CaptureType.Text, text, null, result.Source);
            item = await store.AppendCaptureAsync(item);
            log.Event("capture.text.saved");
            return new(item, "Text saved");
        }
        finally { gate.Release(); }
    }
    public async Task<CaptureOutcome> CaptureImageAsync(Guid sessionId, byte[] png, SourceContext source)
    {
        await gate.WaitAsync();
        string? path = null;
        try
        {
            var session = await store.GetOpenSessionAsync();
            if (Unavailable(session) is { } unavailable) return unavailable;
            if (session!.Id != sessionId) return new(null, "The session changed. Try capturing again.");
            var id = Guid.NewGuid();
            path = await images.SaveAsync(id, png);
            var item = NewItem(session, CaptureType.Image, null, path, source) with { Id = id };
            item = await store.AppendCaptureAsync(item);
            path = null;
            log.Event("capture.image.saved");
            return new(item, "Screenshot saved");
        }
        finally
        {
            try { if (path != null) await images.DeleteAsync(path); }
            finally { gate.Release(); }
        }
    }
    public async Task UndoAsync(CaptureItem item)
    {
        await store.DeleteCaptureAsync(item.Id);
        if (item.ImageRelativePath != null) await images.DeleteAsync(item.ImageRelativePath);
        log.Event("capture.removed");
    }
    public static CaptureOutcome? Unavailable(CaptureSession? session) => session switch
    {
        null or { Status: SessionStatus.Ended } => new(null, "No active notebook. Start a session to save captures.", OpenApp: true),
        { Status: SessionStatus.Paused } => new(null, "Capture is paused.", OpenApp: true),
        _ => null
    };
    private static CaptureItem NewItem(CaptureSession session, CaptureType type, string? text, string? path, SourceContext? source)
    {
        var now = DateTime.UtcNow;
        return new(Guid.NewGuid(), session.NotebookId, session.Id, type, text, path, null, source?.AppName, source?.WindowTitle, null, null, null, now, 0, now, now);
    }
}

using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media.Imaging;
using Clips.Core;
using Clips.Infrastructure;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Clips.App;

public sealed record NotebookRow(Notebook Model, int Count, bool HasSession)
{
    public string Name => Model.Name;
    public string? Icon => Model.Icon;
    public string Color => Model.CoverColor;
    public string Subtitle => $"{Count} captures · {Model.UpdatedAtUtc.ToLocalTime():MMM d}";
    public string Badge => HasSession ? "● Session open" : "";
}
public partial class CaptureViewModel : ObservableObject
{
    public CaptureItem Item { get; private set; }
    private readonly ShellViewModel shell;
    public CaptureViewModel(CaptureItem item, ShellViewModel shell, IImageStorage images)
    {
        Item = item; this.shell = shell; note = item.UserNote ?? "";
        ImagePath = item.ImageRelativePath == null ? null : images.Resolve(item.ImageRelativePath);
        if (ImagePath != null) _ = LoadPreviewAsync(ImagePath);
        SaveNoteCommand = new AsyncRelayCommand(SaveNoteAsync);
        DeleteCommand = new AsyncRelayCommand(() => shell.Safe(async () =>
        {
            if (!Dialogs.Confirm("Delete this capture?", "The capture and its attached note will be removed.", "Delete capture")) return;
            await shell.CapturesService.UndoAsync(Item); shell.ForgetCapture(Item.Id); await shell.RefreshAsync();
        }));
        MoveUpCommand = new AsyncRelayCommand(() => Move(-1)); MoveDownCommand = new AsyncRelayCommand(() => Move(1));
        ExpandCommand = new RelayCommand(() => { if (ImagePath != null) Dialogs.ShowImage(ImagePath); });
    }
    private Task Move(int direction) => shell.Safe(async () => { await shell.Store.MoveCaptureAsync(Item.Id, direction); await shell.RefreshAsync(); });
    [ObservableProperty] private string note;
    [ObservableProperty] private BitmapSource? previewImage;
    private async Task LoadPreviewAsync(string path)
    {
        try
        {
            var bytes = await File.ReadAllBytesAsync(path);
            PreviewImage = await Task.Run(() =>
            {
                using var stream = new MemoryStream(bytes); var bitmap = new BitmapImage(); bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad; bitmap.DecodePixelWidth = 1000; bitmap.StreamSource = stream;
                bitmap.EndInit(); bitmap.Freeze(); return bitmap;
            });
        }
        catch (IOException) { shell.Error = "A screenshot file is missing or unreadable. The capture and its note are still available."; }
        catch (UnauthorizedAccessException) { shell.Error = "A screenshot file could not be opened. Check its file permissions."; }
        catch (NotSupportedException) { shell.Error = "A screenshot file could not be decoded."; }
    }
    private bool savingNote;
    private string? noteSaveError;
    private bool noteSaved;
    private async Task SaveNoteAsync()
    {
        // Snapshot the draft: edits made while the database write awaits remain unsaved.
        var draft = Note;
        shell.Error = "";
        savingNote = true; noteSaveError = null; noteSaved = false; NotifyNoteState();
        try
        {
            var saved = Rules.Note(draft);
            await shell.Store.SaveNoteAsync(Item.Id, saved);
            Item = Item with { UserNote = saved };
            noteSaved = true;
        }
        catch (Exception ex)
        {
            shell.ReportFailure(ex);
            noteSaveError = "Note wasn’t saved. Your draft is still here. Try Save note again.";
        }
        finally { savingNote = false; NotifyNoteState(); }
    }
    private void NotifyNoteState()
    {
        OnPropertyChanged(nameof(IsDirty)); OnPropertyChanged(nameof(NoteStatus));
    }
    partial void OnNoteChanged(string value)
    {
        noteSaveError = null; noteSaved = false; NotifyNoteState();
    }
    public bool IsDirty => Note != (Item.UserNote ?? "");
    public string NoteStatus => savingNote ? "Saving note…" : noteSaveError ??
        (IsDirty ? "Unsaved note · press Save note" : noteSaved ? "Note saved successfully on this device" : "Note saved locally");
    public bool IsText => Item.Type == CaptureType.Text;
    public bool IsImage => Item.Type == CaptureType.Image;
    public string? Text => Item.ContentText;
    public string Preview => IsText ? Text ?? "" : "Screenshot";
    public string? ImagePath { get; }
    public string Caption => $"{Item.CapturedAtUtc.ToLocalTime():MMM d, h:mm tt} · {Item.SourceAppName ?? "Unknown app"}";
    public string? SourceTitle => Item.SourceWindowTitle;
    public string Number => $"#{Item.DisplayOrder:00}";
    public IAsyncRelayCommand SaveNoteCommand { get; }
    public IAsyncRelayCommand DeleteCommand { get; }
    public IAsyncRelayCommand MoveUpCommand { get; }
    public IAsyncRelayCommand MoveDownCommand { get; }
    public IRelayCommand ExpandCommand { get; }
    public void Update(CaptureItem item) { Item = item; OnPropertyChanged(nameof(Number)); NotifyNoteState(); }
}
public partial class ShellViewModel : ObservableObject
{
    public INotebookStore Store { get; }
    public CaptureService CapturesService { get; }
    public CompanionFeedback Feedback { get; }
    private readonly NotebookService notebooksService;
    private readonly IImageStorage images;
    private readonly ILocalLog log;
    private readonly Dictionary<Guid, CaptureViewModel> captureCache = [];
    private readonly SemaphoreSlim refreshGate = new(1, 1);
    public bool HasUnsavedNotes => captureCache.Values.Any(c => c.IsDirty);
    public event Action? StateChanged;
    public event Action? SessionStarted;
    public event Action<int>? SessionEnded;
    public event Action? SettingsRequested;
    public event Action? QuitRequested;
    public event Action<CaptureViewModel>? CaptureOpened;
    public ObservableCollection<NotebookRow> Notebooks { get; } = [];
    public ObservableCollection<CaptureViewModel> Captures { get; } = [];
    public ObservableCollection<CaptureViewModel> Recent { get; } = [];
    public string AppName => Product.Name;
    [ObservableProperty] private NotebookRow? selectedNotebook;
    [ObservableProperty] private CaptureViewModel? selectedCapture;
    [ObservableProperty] private string error = "";
    [ObservableProperty] private bool showArchived;
    private bool refreshing;
    public CaptureSession? Session { get; private set; }
    public Notebook? SessionNotebook { get; private set; }
    public Notebook? ReviewNotebook { get; private set; }
    public bool HasOpenSession => Session != null;
    public bool HasNotebook => SelectedNotebook != null;
    public bool IsEmpty => SelectedNotebook == null;
    public bool NoCaptures => HasNotebook && Captures.Count == 0;
    public string Title => SelectedNotebook?.Name ?? "Your ideas, kept close.";
    public string? Icon => SelectedNotebook?.Icon;
    public string Cover => SelectedNotebook?.Color ?? Product.CoverColors[0];
    public string CountLabel => $"{Captures.Count} captures · stored on this device";
    public string SessionLabel => Session is { } current && current.NotebookId == SelectedNotebook?.Model.Id ? $"● {current.Status}" : "No session in this notebook";
    public string TrayLabel => (SessionNotebook ?? ReviewNotebook)?.Name ?? Product.Name;
    public string TrayState => Session?.Status.ToString() ?? (ReviewNotebook != null ? "Session ended" : "Idle");
    public string TrayColor => SessionNotebook?.CoverColor ?? "#808080";
    public string PauseLabel => Session?.Status == SessionStatus.Paused ? "Resume session" : "Pause session";
    public bool CanStart => SelectedNotebook is { Model.IsArchived: false } && Session?.NotebookId != SelectedNotebook.Model.Id;
    public bool CanManageSession => Session != null && Session.NotebookId == SelectedNotebook?.Model.Id;
    public string ArchiveLabel => ShowArchived ? "Show notebooks" : "Archived notebooks";
    public IAsyncRelayCommand NewNotebookCommand { get; }
    public IAsyncRelayCommand EditNotebookCommand { get; }
    public IAsyncRelayCommand ArchiveCommand { get; }
    public IAsyncRelayCommand DeleteNotebookCommand { get; }
    public IAsyncRelayCommand StartCommand { get; }
    public IAsyncRelayCommand PauseCommand { get; }
    public IAsyncRelayCommand EndCommand { get; }
    public IAsyncRelayCommand ToggleArchiveCommand { get; }
    public IRelayCommand SettingsCommand { get; }
    public IRelayCommand QuitCommand { get; }
    public ShellViewModel(INotebookStore store, CaptureService captures, NotebookService notebooks, IImageStorage images, ILocalLog log)
    {
        Store = store; CapturesService = captures; notebooksService = notebooks; this.images = images; this.log = log;
        Feedback = new CompanionFeedback(ReportFailure, () => Session?.Status switch
        {
            SessionStatus.Active => "Ready to capture", SessionStatus.Paused => "Session paused",
            _ => "Capture is off · notes stay available"
        });
        Feedback.Clear();
        NewNotebookCommand = new AsyncRelayCommand(() => Safe(async () =>
        {
            var result = Dialogs.EditNotebook(null); if (result == null) return;
            var n = await notebooksService.CreateAsync(result.Name, result.Color, result.Icon); ShowArchived = false; await RefreshAsync(n.Id);
        }));
        EditNotebookCommand = new AsyncRelayCommand(() => Safe(async () =>
        {
            if (SelectedNotebook == null) return; var n = SelectedNotebook.Model; var result = Dialogs.EditNotebook(n); if (result == null) return;
            await notebooksService.UpdateAsync(n, result.Name, result.Color, result.Icon); await RefreshAsync(n.Id);
        }));
        ArchiveCommand = new AsyncRelayCommand(() => Safe(async () =>
        {
            if (SelectedNotebook == null) return; var n = SelectedNotebook.Model;
            await Store.SaveNotebookAsync(n with { IsArchived = !n.IsArchived, UpdatedAtUtc = DateTime.UtcNow }); await RefreshAsync();
        }));
        DeleteNotebookCommand = new AsyncRelayCommand(() => Safe(async () =>
        {
            if (SelectedNotebook == null || !Dialogs.Confirm("Delete this notebook permanently?", "All captures and notes in this notebook will be removed. This cannot be undone.", "Delete notebook")) return;
            var n = SelectedNotebook.Model; var items = await Store.GetCapturesAsync(n.Id); await Store.DeleteNotebookAsync(n.Id);
            foreach (var item in items) { ForgetCapture(item.Id); if (item.ImageRelativePath != null) await images.DeleteAsync(item.ImageRelativePath); }
            await RefreshAsync();
        }));
        StartCommand = new AsyncRelayCommand(() => Safe(StartAsync));
        PauseCommand = new AsyncRelayCommand(() => Safe(TogglePauseAsync));
        EndCommand = new AsyncRelayCommand(() => Safe(EndAsync));
        ToggleArchiveCommand = new AsyncRelayCommand(() => Safe(async () => { ShowArchived = !ShowArchived; await RefreshAsync(); }));
        SettingsCommand = new RelayCommand(() => SettingsRequested?.Invoke());
        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke());
    }
    partial void OnSelectedNotebookChanged(NotebookRow? value)
    {
        if (!refreshing) _ = Safe(async () => { await LoadCapturesAsync(); NotifyState(); });
    }
    public async Task Safe(Func<Task> work)
    {
        try { Error = ""; await work(); }
        catch (Exception ex) { ReportFailure(ex); }
    }
    internal void ReportFailure(Exception ex)
    {
        log.Event("operation.failed", ex.HResult);
        Error = ex is ArgumentException or InvalidOperationException ? ex.Message : "Couldn’t save this change. Check that the local data folder is writable and try again.";
    }
    public async Task RefreshAsync(Guid? selectId = null)
    {
        await refreshGate.WaitAsync();
        try
        {
        Session = await Store.GetOpenSessionAsync();
        var available = await Store.GetNotebooksAsync();
        SessionNotebook = available.FirstOrDefault(n => n.Id == Session?.NotebookId);
        ReviewNotebook = SessionNotebook ?? (ReviewNotebook == null ? null :
            available.FirstOrDefault(n => n.Id == ReviewNotebook.Id) ??
            (await Store.GetNotebooksAsync(true)).FirstOrDefault(n => n.Id == ReviewNotebook.Id));
        var list = ShowArchived ? await Store.GetNotebooksAsync(true) : available;
        var id = selectId ?? SelectedNotebook?.Model.Id;
        var rows = new List<NotebookRow>(); foreach (var n in list) rows.Add(new(n, (await Store.GetCapturesAsync(n.Id)).Count, n.Id == Session?.NotebookId));
        refreshing = true;
        try { Notebooks.Clear(); foreach (var row in rows) Notebooks.Add(row); SelectedNotebook = rows.FirstOrDefault(n => n.Model.Id == id) ?? rows.FirstOrDefault(); }
        finally { refreshing = false; }
        await LoadCapturesAsync();
        Recent.Clear();
        if (ReviewNotebook != null)
            foreach (var item in (await Store.GetCapturesAsync(ReviewNotebook.Id)).OrderByDescending(i => i.CapturedAtUtc).ThenByDescending(i => i.DisplayOrder).Take(5))
                Recent.Add(CaptureVm(item));
        NotifyState();
        }
        finally { refreshGate.Release(); }
    }
    private CaptureViewModel CaptureVm(CaptureItem item)
    {
        if (!captureCache.TryGetValue(item.Id, out var vm)) { vm = new CaptureViewModel(item, this, images); captureCache.Add(item.Id, vm); }
        else vm.Update(item);
        return vm;
    }
    public void ForgetCapture(Guid id) => captureCache.Remove(id);
    private async Task LoadCapturesAsync()
    {
        var notebook = SelectedNotebook;
        var items = notebook == null ? [] : await Store.GetCapturesAsync(notebook.Model.Id);
        if (SelectedNotebook?.Model.Id != notebook?.Model.Id) return;
        Captures.Clear();
        foreach (var item in items)
        {
            Captures.Add(CaptureVm(item));
        }
    }
    private void NotifyState()
    {
        foreach (var name in new[] { nameof(HasNotebook), nameof(IsEmpty), nameof(NoCaptures), nameof(CountLabel), nameof(Title), nameof(Icon), nameof(Cover), nameof(SessionLabel), nameof(TrayLabel), nameof(TrayState), nameof(TrayColor), nameof(PauseLabel), nameof(CanStart), nameof(CanManageSession), nameof(HasOpenSession), nameof(ArchiveLabel) }) OnPropertyChanged(name);
        StateChanged?.Invoke();
    }
    private async Task StartAsync()
    {
        if (!CanStart || SelectedNotebook == null) return; var n = SelectedNotebook.Model; var replace = false;
        var session = await Store.GetOpenSessionAsync();
        if (session != null)
        {
            var name = (await Store.GetNotebooksAsync()).FirstOrDefault(x => x.Id == session.NotebookId)?.Name ?? "another notebook";
            replace = Dialogs.Confirm($"You already have an active session in ‘{name}’.", "End that session and start one here?", "End and start this session", "Keep current session");
            if (!replace) return;
        }
        await Store.StartSessionAsync(n.Id, replace); await RefreshAsync(n.Id); SessionStarted?.Invoke();
    }
    public async Task TogglePauseAsync()
    {
        if (Session == null) return;
        await Store.TransitionSessionAsync(Session.Id, Session.Status == SessionStatus.Active ? SessionStatus.Paused : SessionStatus.Active); await RefreshAsync();
    }
    public async Task EndAsync()
    {
        if (Session == null) return;
        var session = Session;
        var count = (await Store.GetCapturesAsync(session.NotebookId)).Count(c => c.SessionId == session.Id);
        await Store.TransitionSessionAsync(session.Id, SessionStatus.Ended); await RefreshAsync(); SessionEnded?.Invoke(count);
    }
    public async Task OpenCaptureAsync(CaptureItem item)
    {
        ShowArchived = false; await RefreshAsync(item.NotebookId); SelectedCapture = Captures.FirstOrDefault(c => c.Item.Id == item.Id);
        if (SelectedCapture != null) CaptureOpened?.Invoke(SelectedCapture);
    }
}

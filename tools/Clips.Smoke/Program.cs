using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation.Peers;
using System.Windows.Automation.Provider;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clips.App;
using Clips.Core;
using Clips.Infrastructure;
using Microsoft.Data.Sqlite;

namespace Clips.Smoke;
// Developer-only runtime smoke check, using fabricated content and an isolated temporary database.
public static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        if (File.Exists(Path.Combine(AppContext.BaseDirectory, "selection-probe.flag")))
        {
            var folder = Path.Combine(AppContext.BaseDirectory, "selection-probe"); Directory.CreateDirectory(folder);
            Thread.Sleep(3000); // Give the QA operator time to return to the synthetic PDF.
            var selection = new SelectedTextCaptureService(new ForegroundWindowContextService(), new ProbeLog(folder)).ReadAsync().GetAwaiter().GetResult();
            File.WriteAllText(Path.Combine(folder, "result.txt"), $"Status: {selection.Status}\nSynthetic selection matched: {selection.Text?.Trim() == "Clips PDF selection probe"}\n");
            return selection.Status == SelectionStatus.Success ? 0 : 1;
        }
        var interactive = args.Contains("--interactive") || File.Exists(Path.Combine(AppContext.BaseDirectory, "interactive.flag"));
        var output = Path.GetFullPath(args.FirstOrDefault() ?? (interactive ? Path.Combine(AppContext.BaseDirectory, "interaction") : "artifacts/smoke")); Directory.CreateDirectory(output);
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("pack://application:,,,/Clips.App;component/Theme.xaml") });
        var errors = new BindingErrorCounter(); PresentationTraceSources.DataBindingSource.Listeners.Add(errors);
        var code = 1;
        app.Startup += async (_, _) =>
        {
            var paths = new AppPaths(Path.Combine(Path.GetTempPath(), "ClipsSmoke", Guid.NewGuid().ToString("D")));
            using var store = new SqliteNotebookStore(paths); var images = new ImageStorage(paths); var log = new SmokeLog();
            try
            {
                await store.InitializeAsync(); var notebookService = new NotebookService(store);
                var captures = new CaptureService(store, images, new SmokeReader(), log); var shell = new ShellViewModel(store, captures, notebookService, images, log);
                await shell.RefreshAsync(); var main = new MainWindow(shell); main.Show(); await Render(main, output, "first-launch");
                var notebook = await notebookService.CreateAsync("Field notes", "#2B897F", "🌿"); var session = await store.StartSessionAsync(notebook.Id);
                await captures.CaptureTextAsync(); var bytes = DemoPng(); var imageCapture = (await captures.CaptureImageAsync(session.Id, bytes, new("Demo app", "A captured figure"))).Item!;
                await shell.RefreshAsync(notebook.Id); await Render(main, output, "notebook");
                await VerifyNoteSaving(main, shell, paths, notebook.Id, output);
                using (var hotkeys = new GlobalHotkeyService()) hotkeys.Apply(new(), false);
                var tray = new CaptureTray(shell, () => main.Show());
                tray.Show(); await Render(tray, output, "capture-tray");
                if (interactive)
                {
                    main.Title = "Clips keyboard QA";
                    // Expose the otherwise taskbar-hidden tool window to the desktop QA driver.
                    tray.ShowInTaskbar = true;
                    var trayHandle = new WindowInteropHelper(tray).Handle;
                    SetWindowLong(trayHandle, -20, (GetWindowLong(trayHandle, -20) & ~0x80) | 0x40000);
                    tray.Left = main.Left + main.Width - tray.Width - 35; tray.Top = main.Top + 80;
                    await File.WriteAllTextAsync(Path.Combine(output, "ready.txt"), "Isolated capture tray ready for keyboard-input QA.");
                    var deadline = DateTime.UtcNow.AddMinutes(15);
                    while (!File.Exists(Path.Combine(output, "finish.txt")) && DateTime.UtcNow < deadline) await Task.Delay(500);
                    var edited = await store.GetCapturesAsync(notebook.Id);
                    if (edited.Single(c => c.Id == imageCapture.Id).UserNote != "Screenshot note keyboard test") throw new InvalidOperationException("The screenshot note did not persist after keyboard input.");
                    if (edited.Single(c => c.Type == CaptureType.Text).UserNote != "Full notebook keyboard test") throw new InvalidOperationException("The full-notebook note did not persist after keyboard input.");
                    await File.WriteAllTextAsync(Path.Combine(output, "keyboard-result.txt"), "Screenshot note typing/saving in capture tray: passed\nText note typing/saving in full notebook: passed\n");
                }
                tray.AllowClose = true; tray.Close();
                var settings = new SettingsWindow(new(), paths, _ => Task.FromResult("Saved")); settings.Show(); await Render(settings, output, "settings"); settings.Close();
                var toast = new ToastWindow("Text saved to Field notes", [("Undo", () => { }), ("Open", () => { })]); toast.Show(); await Render(toast, output, "toast"); toast.Close();
                var bitmap = BitmapFrom(bytes); var snip = new SnippingWindow(new(bitmap, 0, 0, 300, 200, 1)); snip.Show(); await Render(snip, output, "snipping"); snip.Close();
                main.AllowClose = true; main.Close();
                await captures.UndoAsync(imageCapture);
                if (File.Exists(images.Resolve(imageCapture.ImageRelativePath!))) throw new InvalidOperationException("Image deletion failed.");
                await File.WriteAllTextAsync(Path.Combine(output, "result.txt"), $"WPF views constructed and rendered: 6\nBinding errors: {errors.Count}\nSQLite: text/image captures persisted\nImage Undo after rendering: passed\nIsolated test data: removed on exit\n");
                code = errors.Count == 0 ? 0 : 1;
            }
            catch (Exception ex) { await File.WriteAllTextAsync(Path.Combine(output, "failure.txt"), ex.ToString()); }
            finally { store.Dispose(); Directory.Delete(paths.Root, true); app.Shutdown(code); }
        };
        app.Run(); return code;
    }
    private static async Task Render(Window window, string output, string name)
    {
        await Task.Delay(400); window.UpdateLayout();
        var rendered = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32); rendered.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(rendered));
        await using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
    }
    private static async Task VerifyNoteSaving(MainWindow main, ShellViewModel shell, AppPaths paths, Guid notebookId, string output)
    {
        var capture = shell.Captures.Single(c => c.IsText);
        var timeline = (ListBox)main.FindName("Timeline");
        capture.Note = string.Join("\n", Enumerable.Repeat("A multiline draft", 30));
        main.UpdateLayout();
        var scroll = Descendants<ScrollViewer>(timeline).First();
        timeline.UnselectAll(); scroll.ScrollToVerticalOffset(80);
        await Task.Delay(100); main.UpdateLayout();
        var offset = scroll.VerticalOffset;
        if (offset <= 0) throw new InvalidOperationException("The note regression fixture must be scrolled.");
        timeline.SelectedItem = capture; main.UpdateLayout(); await Task.Delay(100);
        if (Math.Abs(scroll.VerticalOffset - offset) > 0.1) throw new InvalidOperationException("Selecting a note card moved its Save button.");
        // Invoke the real template button, rather than calling the store directly.
        var save = Descendants<Button>(timeline).Single(b => Equals(b.Content, "Save note") && ReferenceEquals(b.DataContext, capture));
        var expected = capture.Note;
        ((IInvokeProvider)new ButtonAutomationPeer(save).GetPattern(PatternInterface.Invoke)).Invoke();
        await Task.Delay(100);
        if (capture.SaveNoteCommand.ExecutionTask is { } task) await task;
        if (capture.IsDirty || !capture.NoteStatus.Contains("successfully")) throw new InvalidOperationException("Save note did not confirm persistence.");
        using (var reopened = new SqliteNotebookStore(paths))
        {
            await reopened.InitializeAsync();
            if ((await reopened.GetCapturesAsync(notebookId)).Single(c => c.Id == capture.Item.Id).UserNote != expected)
                throw new InvalidOperationException("Saved text note did not survive reopening the database.");
        }
        // Inject a write failure to verify that the draft remains editable and retry works.
        using (var connection = new SqliteConnection($"Data Source={paths.Database};Pooling=False"))
        {
            connection.Open(); using var command = connection.CreateCommand();
            command.CommandText = "CREATE TRIGGER smoke_note_failure BEFORE UPDATE OF UserNote ON CaptureItem BEGIN SELECT RAISE(ABORT, 'injected failure'); END;";
            command.ExecuteNonQuery();
            capture.Note = "Draft retained after a failed save";
            await capture.SaveNoteCommand.ExecuteAsync(null);
            if (!capture.IsDirty || !capture.NoteStatus.Contains("wasn’t saved") || capture.Note != "Draft retained after a failed save")
                throw new InvalidOperationException("Failed save lost its draft or lacked inline feedback.");
            command.CommandText = "DROP TRIGGER smoke_note_failure;"; command.ExecuteNonQuery();
        }
        await capture.SaveNoteCommand.ExecuteAsync(null);
        if (capture.IsDirty) throw new InvalidOperationException("Retrying the failed note save failed.");
        using (var connection = new SqliteConnection($"Data Source={paths.Database};Pooling=False"))
        {
            connection.Open(); using var transaction = connection.BeginTransaction();
            using var command = connection.CreateCommand(); command.Transaction = transaction;
            command.CommandText = "UPDATE Notebook SET UpdatedAtUtc=UpdatedAtUtc;"; command.ExecuteNonQuery();
            capture.Note = "Snapshot submitted for saving";
            var pendingSave = capture.SaveNoteCommand.ExecuteAsync(null);
            await Task.Delay(100);
            if (!capture.SaveNoteCommand.IsRunning || capture.SaveNoteCommand.CanExecute(null) || capture.NoteStatus != "Saving note…")
                throw new InvalidOperationException("Pending save lacks feedback or permits duplicate writes.");
            capture.Note = "New edit while save is pending";
            transaction.Commit(); await pendingSave;
            if (!capture.IsDirty || capture.Note != "New edit while save is pending" ||
                (await shell.Store.GetCapturesAsync(notebookId)).Single(c => c.Id == capture.Item.Id).UserNote != "Snapshot submitted for saving")
                throw new InvalidOperationException("An edit made during saving was lost or incorrectly marked saved.");
        }
        await capture.SaveNoteCommand.ExecuteAsync(null);
        capture.Note = ""; await capture.SaveNoteCommand.ExecuteAsync(null);
        if ((await shell.Store.GetCapturesAsync(notebookId)).Single(c => c.Id == capture.Item.Id).UserNote != null)
            throw new InvalidOperationException("Clearing a saved note failed.");
        await shell.RefreshAsync(notebookId);
        if (capture.IsDirty) throw new InvalidOperationException("Refresh marked a saved note unsaved.");
        await shell.OpenCaptureAsync(capture.Item); await Task.Delay(100); main.UpdateLayout();
        if (!ReferenceEquals(timeline.SelectedItem, capture) || scroll.VerticalOffset >= offset)
            throw new InvalidOperationException("Explicit Open capture did not navigate to the requested card.");
        await File.WriteAllTextAsync(Path.Combine(output, "note-save-result.txt"), "Card selection preserves scroll position: passed\nTemplate Save note button persists multiline text: passed\nSaved note survives database reopen: passed\nFailed write retains draft and displays error: passed\nPending save blocks duplicates and preserves new edits: passed\nRetry, clear, and refresh: passed\nExplicit Open capture navigation: passed\n");
    }
    private static IEnumerable<T> Descendants<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) yield return match;
            foreach (var nested in Descendants<T>(child)) yield return nested;
        }
    }
    private static byte[] DemoPng()
    {
        var visual = new DrawingVisual(); using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(Brushes.WhiteSmoke, null, new Rect(0, 0, 300, 200));
            dc.DrawEllipse(Brushes.MediumSeaGreen, null, new Point(150, 100), 70, 70);
            dc.DrawRectangle(Brushes.White, null, new Rect(145, 55, 10, 90));
        }
        var image = new RenderTargetBitmap(300, 200, 96, 96, PixelFormats.Pbgra32); image.Render(visual); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image)); using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }
    private static BitmapSource BitmapFrom(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes); var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.StreamSource = stream; image.EndInit(); image.Freeze(); return image;
    }
    private sealed class SmokeReader : ISelectedTextReader
    {
        public Task<SelectionResult> ReadAsync() => Task.FromResult(new SelectionResult(SelectionStatus.Success, "The best observations start with noticing.\n\nKeep the source close, and add your own thought when it matters.", new("notepad", "Reading notes — demo")));
    }
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint handle, int index, int value);
    private sealed class SmokeLog : ILocalLog { public void Event(string eventName, int? errorCode = null) { } }
    private sealed class ProbeLog(string folder) : ILocalLog
    {
        public void Event(string eventName, int? errorCode = null) => File.AppendAllText(Path.Combine(folder, "events.txt"), $"{eventName} {errorCode}\n");
    }
    private sealed class BindingErrorCounter : TraceListener
    {
        public int Count { get; private set; }
        public override void Write(string? message) { }
        public override void WriteLine(string? message) => Count++;
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clips.App;
using Clips.Core;
using Clips.Infrastructure;

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

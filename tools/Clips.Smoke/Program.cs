using System.Diagnostics;
using System.Windows;
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
        var output = Path.GetFullPath(args.FirstOrDefault() ?? "artifacts/smoke"); Directory.CreateDirectory(output);
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
                var tray = new CaptureTray(shell, () => { }); tray.Show(); await Render(tray, output, "capture-tray"); tray.AllowClose = true; tray.Close();
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
    private sealed class SmokeLog : ILocalLog { public void Event(string eventName, int? errorCode = null) { } }
    private sealed class BindingErrorCounter : TraceListener
    {
        public int Count { get; private set; }
        public override void Write(string? message) { }
        public override void WriteLine(string? message) => Count++;
    }
}

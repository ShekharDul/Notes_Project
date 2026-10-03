using System.Windows;
using System.Windows.Media;
using Clips.Core;
using Clips.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Win32;
using Forms = System.Windows.Forms;
using Drawing = System.Drawing;

namespace Clips.App;
public partial class App : Application
{
    private ServiceProvider? services;
    private ShellViewModel shell = null!;
    private MainWindow main = null!;
    private CaptureTray tray = null!;
    private GlobalHotkeyService hotkeys = null!;
    private AppSettings settings = new();
    private Forms.NotifyIcon? icon;
    private Drawing.Icon? stateIcon;
    private ToastWindow? toast;
    private Mutex? singleInstance;
    private bool ownsMutex;
    private bool capturing;
    private bool shuttingDown;
    private IReadOnlyList<string> hotkeyErrors = [];
    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        singleInstance = new Mutex(true, $"Local\\{Product.StorageDirectoryName}.Desktop.SingleInstance", out ownsMutex);
        if (!ownsMutex) { MessageBox.Show($"{Product.Name} is already running. Open it from the Windows system tray.", Product.Name); Shutdown(); return; }
        try
        {
            var collection = new ServiceCollection();
            collection.AddSingleton<AppPaths>(); collection.AddSingleton<ILocalLog, LocalLog>();
            collection.AddSingleton<INotebookStore, SqliteNotebookStore>(); collection.AddSingleton<ImageStorage>();
            collection.AddSingleton<IImageStorage>(s => s.GetRequiredService<ImageStorage>());
            collection.AddSingleton<ForegroundWindowContextService>(); collection.AddSingleton<ISelectedTextReader, SelectedTextCaptureService>();
            collection.AddSingleton<ScreenSnapshotService>(); collection.AddSingleton<SnippingService>();
            collection.AddSingleton<NotebookService>(); collection.AddSingleton<CaptureService>(); collection.AddSingleton<ShellViewModel>();
            services = collection.BuildServiceProvider();
            var store = services.GetRequiredService<INotebookStore>(); await store.InitializeAsync();
            await services.GetRequiredService<ImageStorage>().ReconcileAsync(store);
            settings = await store.GetSettingsAsync(); ApplyTheme();
            shell = services.GetRequiredService<ShellViewModel>(); await shell.RefreshAsync();
            main = new MainWindow(shell); MainWindow = main;
            tray = new CaptureTray(shell, () => _ = Guard(async () => { if (shell.Session != null) await shell.RefreshAsync(shell.Session.NotebookId); ShowMain(); }));
            hotkeys = new GlobalHotkeyService(); hotkeys.Pressed += id => _ = HandleHotkeyAsync(id);
            shell.StateChanged += SyncSession; shell.SessionStarted += OnSessionStarted; shell.SettingsRequested += ShowSettings;
            shell.SessionEnded += count => Notify($"Session ended. {count} captures saved.", []);
            CreateSystemTray(); SyncSession();
            SystemEvents.UserPreferenceChanged += OnUserPreferences;
            DispatcherUnhandledException += (_, args) =>
            {
                services.GetRequiredService<ILocalLog>().Event("ui.unhandled", args.Exception.HResult);
                args.Handled = true; Notify("Something went wrong. Open the notebook to check whether your change was saved.", [("Open " + Product.Name, ShowMain)], true);
            };
            services.GetRequiredService<ILocalLog>().Event("application.started");
            if (shell.Session == null && (!settings.StartMinimizedToTray || shell.Notebooks.Count == 0)) ShowMain();
            if (hotkeyErrors.Count > 0) Notify(string.Join(" ", hotkeyErrors), [("Settings", ShowSettings)], true);
        }
        catch (Exception ex)
        {
            services?.GetService<ILocalLog>()?.Event("startup.failed", ex.HResult);
            MessageBox.Show("Couldn’t open local storage. Check that the data folder is writable and that no other instance is running. Your existing data has not been reset.", Product.Name);
            Shutdown(1);
        }
    }
    private void ShowMain() { main.Show(); main.WindowState = WindowState.Normal; main.Activate(); }
    private void ShowSettings()
    {
        var window = new SettingsWindow(settings, services!.GetRequiredService<AppPaths>(), async next =>
        {
            Hotkey.Validate(next); await shell.Store.SaveSettingsAsync(next); settings = next; ApplyTheme(); SyncSession();
            return hotkeyErrors.Count > 0 ? string.Join("\n", hotkeyErrors) : "Settings saved. Shortcuts will be active during a session.";
        }); window.Show(); window.Activate();
    }
    private void OnSessionStarted()
    {
        main.Hide(); Notify($"Session started — saving to {shell.TrayLabel}.", []);
        if (hotkeyErrors.Count > 0) Notify(string.Join(" ", hotkeyErrors), [("Settings", ShowSettings)], true);
    }
    private void SyncSession()
    {
        if (shuttingDown) return;
        hotkeyErrors = hotkeys.Apply(settings, shell.Session?.Status == SessionStatus.Active);
        if (hotkeyErrors.Count > 0) shell.Error = string.Join("\n", hotkeyErrors);
        if (icon == null) return;
        var state = shell.Session?.Status.ToString() ?? "Idle";
        icon.Text = $"{Product.Name} — {state}";
        var color = shell.Session?.Status switch { SessionStatus.Active => Drawing.Color.MediumSeaGreen, SessionStatus.Paused => Drawing.Color.Goldenrod, _ => Drawing.Color.SlateGray };
        var old = stateIcon; stateIcon = MakeIcon(color, state); icon.Icon = stateIcon; old?.Dispose();
        var menu = icon.ContextMenuStrip!;
        menu.Items[2].Text = shell.PauseLabel; menu.Items[2].Enabled = shell.Session != null; menu.Items[3].Enabled = shell.Session != null;
    }
    private static Drawing.Icon MakeIcon(Drawing.Color color, string state)
    {
        using var bitmap = new Drawing.Bitmap(32, 32); using var graphics = Drawing.Graphics.FromImage(bitmap);
        graphics.SmoothingMode = Drawing.Drawing2D.SmoothingMode.AntiAlias;
        using var brush = new Drawing.SolidBrush(color); graphics.FillEllipse(brush, 2, 2, 28, 28);
        using var ink = new Drawing.SolidBrush(Drawing.Color.White);
        if (state == "Paused") { graphics.FillRectangle(ink, 11, 9, 3, 14); graphics.FillRectangle(ink, 18, 9, 3, 14); }
        else if (state == "Active") graphics.FillEllipse(ink, 10, 10, 12, 12);
        else graphics.FillRectangle(ink, 10, 14, 12, 4);
        var handle = bitmap.GetHicon(); try { using var temp = Drawing.Icon.FromHandle(handle); return (Drawing.Icon)temp.Clone(); }
        finally { DestroyIcon(handle); }
    }
    [System.Runtime.InteropServices.DllImport("user32.dll")] private static extern bool DestroyIcon(nint icon);
    private void CreateSystemTray()
    {
        var menu = new Forms.ContextMenuStrip();
        void Add(string label, Action action) => menu.Items.Add(label, null, (_, _) => Dispatcher.InvokeAsync(action));
        Add("Open " + Product.Name, ShowMain); Add("Show capture tray", () => tray.Show());
        Add("Pause capture", () => _ = Guard(shell.TogglePauseAsync));
        Add("End session", () => _ = Guard(async () =>
        {
            if (shell.Session == null || !Dialogs.Confirm("End this capture session?", "Your notebook and saved captures will remain available.", "End session")) return;
            await shell.EndAsync();
        }));
        menu.Items.Add(new Forms.ToolStripSeparator()); Add("Quit", () => _ = QuitAsync());
        icon = new Forms.NotifyIcon { Visible = true, ContextMenuStrip = menu, Text = Product.Name }; icon.DoubleClick += (_, _) => Dispatcher.InvokeAsync(ShowMain);
    }
    private async Task HandleHotkeyAsync(int id)
    {
        if (id == 3) { if (!capturing) tray.Show(); return; }
        if (capturing) return; capturing = true;
        try
        {
            await Guard(async () =>
            {
                CaptureOutcome result;
                if (id == 1) result = await shell.CapturesService.CaptureTextAsync();
                else
                {
                    var session = await shell.Store.GetOpenSessionAsync();
                    if (CaptureService.Unavailable(session) is { } unavailable) { ShowOutcome(unavailable); return; }
                    var context = services!.GetRequiredService<ForegroundWindowContextService>().Read();
                    tray.Hide(); toast?.Close();
                    var bytes = await services!.GetRequiredService<SnippingService>().SnipAsync(); if (bytes == null) return;
                    result = await shell.CapturesService.CaptureImageAsync(session!.Id, bytes, context.Source);
                }
                if (result.Item != null) await shell.RefreshAsync(); ShowOutcome(result);
            });
        }
        finally { capturing = false; }
    }
    private void ShowOutcome(CaptureOutcome outcome)
    {
        var actions = new List<(string, Action)>();
        if (outcome.Item is { } item)
        {
            actions.Add(("Undo", () => _ = Guard(async () => { await shell.CapturesService.UndoAsync(item); shell.ForgetCapture(item.Id); await shell.RefreshAsync(); Notify("Capture removed.", []); })));
            actions.Add(("Open", () => _ = Guard(async () => { await shell.OpenCaptureAsync(item); ShowMain(); })));
            Notify($"{outcome.Message} to {shell.TrayLabel}", actions);
        }
        else
        {
            if (outcome.OfferScreenshot) actions.Add(("Capture screenshot", () => _ = HandleHotkeyAsync(2)));
            if (outcome.OpenApp) actions.Add(("Open " + Product.Name, ShowMain)); Notify(outcome.Message, actions, true);
        }
    }
    private void Notify(string message, IEnumerable<(string Label, Action Run)> actions, bool essential = false)
    {
        if (!settings.NotificationsEnabled && !essential) return;
        toast?.Close(); toast = new ToastWindow(message, actions); toast.Show();
    }
    private async Task Guard(Func<Task> work)
    {
        try { await work(); }
        catch (Exception ex)
        {
            services!.GetRequiredService<ILocalLog>().Event("operation.failed", ex.HResult);
            var message = ex is ArgumentException or InvalidOperationException ? ex.Message : "Capture couldn’t be saved. Check the local storage folder and try again.";
            shell.Error = message; Notify(message, [("Open " + Product.Name, ShowMain)], true);
        }
    }
    private async Task QuitAsync()
    {
        if (capturing) { Notify("Finish or cancel the current capture before quitting.", [], true); return; }
        if (shell.HasUnsavedNotes && !Dialogs.Confirm("Quit with unsaved notes?", "Press Save note before quitting to keep your edits.", "Quit", "Keep editing")) return;
        await Task.CompletedTask; Shutdown();
    }
    private void OnUserPreferences(object sender, UserPreferenceChangedEventArgs e) => Dispatcher.InvokeAsync(ApplyTheme);
    private void ApplyTheme()
    {
        var dark = settings.Theme == AppTheme.Dark;
        if (settings.Theme == AppTheme.System)
        {
            try { dark = Registry.GetValue(@"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) is int value && value == 0; }
            catch (System.Security.SecurityException) { }
        }
        if (SystemParameters.HighContrast)
        {
            Resources["Surface"] = SystemColors.WindowBrush; Resources["Card"] = SystemColors.WindowBrush; Resources["Ink"] = SystemColors.WindowTextBrush;
            Resources["Muted"] = SystemColors.WindowTextBrush; Resources["Line"] = SystemColors.WindowTextBrush; Resources["Accent"] = SystemColors.HighlightBrush; Resources["AccentInk"] = SystemColors.HighlightTextBrush; return;
        }
        var palette = dark ? new[] { "#191D28", "#242A38", "#F0F2F8", "#B3BDCF", "#485166", "#6576E8" } : new[] { "#F7F8FB", "#FFFFFF", "#202536", "#586174", "#D4D9E3", "#5262D5" };
        var names = new[] { "Surface", "Card", "Ink", "Muted", "Line", "Accent" };
        Resources["AccentInk"] = Brushes.White;
        for (var i = 0; i < names.Length; i++) Resources[names[i]] = new SolidColorBrush((Color)ColorConverter.ConvertFromString(palette[i]));
    }
    protected override void OnExit(ExitEventArgs e)
    {
        shuttingDown = true; SystemEvents.UserPreferenceChanged -= OnUserPreferences;
        toast?.Close(); hotkeys?.Dispose();
        if (icon != null) { icon.Visible = false; icon.ContextMenuStrip?.Dispose(); icon.Dispose(); }
        stateIcon?.Dispose();
        if (tray != null) { tray.AllowClose = true; tray.Close(); }
        if (main != null) { main.AllowClose = true; main.Close(); }
        services?.GetService<ILocalLog>()?.Event("application.stopped"); services?.Dispose();
        if (ownsMutex) singleInstance?.ReleaseMutex(); singleInstance?.Dispose(); base.OnExit(e);
    }
}

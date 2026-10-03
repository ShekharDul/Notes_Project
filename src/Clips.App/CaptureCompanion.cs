using System.Windows;

namespace Clips.App;

// Owns the single compact/expanded surface, including restoration after screenshot capture.
public sealed class CaptureCompanion : IDisposable
{
    private readonly ShellViewModel shell;
    private readonly Window main;
    private bool expanded;
    private bool suspended;
    public SessionPanel Panel { get; }
    public CaptureTray Tray { get; }
    public CaptureCompanion(ShellViewModel shell, Window main, Action openNotebook)
    {
        this.shell = shell; this.main = main;
        Panel = new SessionPanel(shell, ShowRecent, openNotebook);
        Tray = new CaptureTray(shell, openNotebook, Collapse);
        main.IsVisibleChanged += MainVisibilityChanged;
        shell.StateChanged += SyncSession;
    }
    private void MainVisibilityChanged(object sender, DependencyPropertyChangedEventArgs e) => SyncSession();
    public void ShowRecent()
    {
        if (suspended) return;
        expanded = true; main.Hide();
        var area = SystemParameters.WorkArea;
        Tray.Left = Math.Clamp(Panel.Left + Panel.Width - Tray.Width, area.Left, Math.Max(area.Left, area.Right - Tray.Width));
        Tray.Top = Math.Clamp(Panel.Top + Panel.Height - Tray.Height, area.Top, Math.Max(area.Top, area.Bottom - Tray.Height));
        SyncSession(); Tray.ShowLatest();
    }
    public void Collapse() { expanded = false; SyncSession(); }
    public void SyncSession()
    {
        if (suspended || main.IsVisible) { Panel.Hide(); Tray.Hide(); return; }
        if (expanded) { Panel.Hide(); Tray.Show(); }
        else { Tray.Hide(); Panel.Show(); }
    }
    public void Suspend() { suspended = true; SyncSession(); }
    public void Resume() { suspended = false; SyncSession(); }
    public void Dispose()
    {
        main.IsVisibleChanged -= MainVisibilityChanged; shell.StateChanged -= SyncSession;
        shell.Feedback.Stop(); Panel.AllowClose = true; Tray.AllowClose = true; Panel.Close(); Tray.Close();
    }
}

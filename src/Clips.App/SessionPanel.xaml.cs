using System.Windows;
using System.Windows.Input;

namespace Clips.App;

public partial class SessionPanel : Window
{
    private readonly Action recent;
    private readonly Action notebook;
    public bool AllowClose { get; set; }
    public SessionPanel(ShellViewModel vm, Action recent, Action notebook)
    {
        InitializeComponent(); Style = (Style)Application.Current.FindResource(typeof(Window));
        DataContext = vm; this.recent = recent; this.notebook = notebook;
        // The launcher must not take focus from the document being read.
        NativeWindows.NonActivating(this, false); NativeWindows.PlaceBottomRight(this);
        Closing += (_, e) => { if (!AllowClose) e.Cancel = true; };
    }
    public void SyncSession()
    {
        if (((ShellViewModel)DataContext).Session != null) Show(); else Hide();
    }
    private void ShowRecent(object sender, RoutedEventArgs e) => recent();
    private void ShowNotebook(object sender, RoutedEventArgs e) => notebook();
    private void DragPanel(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        DragMove();
        var area = SystemParameters.WorkArea;
        Left = Math.Clamp(Left, area.Left, Math.Max(area.Left, area.Right - Width));
        Top = Math.Clamp(Top, area.Top, Math.Max(area.Top, area.Bottom - Height));
    }
}

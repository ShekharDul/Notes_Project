using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Clips.App;
public partial class CaptureTray : Window
{
    private readonly Action open;
    private readonly Action collapse;
    public bool AllowClose { get; set; }
    public CaptureTray(ShellViewModel vm, Action open, Action? collapse = null)
    {
        InitializeComponent(); Style = (Style)Application.Current.FindResource(typeof(Window)); DataContext = vm; this.open = open; NativeWindows.NonActivating(this, true); NativeWindows.PlaceBottomRight(this);
        this.collapse = collapse ?? Hide;
        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; this.collapse(); } };
    }
    private void OpenNotebook(object sender, RoutedEventArgs e) => open();
    private void HideTray(object sender, RoutedEventArgs e) => collapse();
    public void ShowLatest() => RecentScroll.ScrollToTop();
    private void NoteExpanded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander { Content: StackPanel panel } || e.Source != sender) return;
        var editor = panel.Children.OfType<TextBox>().FirstOrDefault();
        if (editor == null) return;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            if (!IsVisible) return;
            Activate(); editor.BringIntoView(); editor.Focus(); Keyboard.Focus(editor);
        }));
    }
}

using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace Clips.App;
public partial class CaptureTray : Window
{
    private readonly Action open;
    public bool AllowClose { get; set; }
    public CaptureTray(ShellViewModel vm, Action open)
    {
        InitializeComponent(); Style = (Style)Application.Current.FindResource(typeof(Window)); DataContext = vm; this.open = open; NativeWindows.NonActivating(this, true); NativeWindows.PlaceBottomRight(this);
        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; Hide(); } };
    }
    private void OpenNotebook(object sender, RoutedEventArgs e) => open();
    private void HideTray(object sender, RoutedEventArgs e) => Hide();
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

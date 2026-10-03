using System.Windows;

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
}

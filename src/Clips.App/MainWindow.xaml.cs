using System.ComponentModel;
using System.Windows;

namespace Clips.App;

public partial class MainWindow : Window
{
    public bool AllowClose { get; set; }
    public MainWindow(ShellViewModel vm)
    {
        InitializeComponent(); Style = (Style)Application.Current.FindResource(typeof(Window)); DataContext = vm;
        Closing += (_, e) => { if (!AllowClose) { e.Cancel = true; Hide(); } };
        // Selecting a card during a mouse press must not move its Save note button
        // before mouse release. Scroll only for an explicit Open capture action.
        vm.CaptureOpened += capture => Timeline.ScrollIntoView(capture);
    }
}

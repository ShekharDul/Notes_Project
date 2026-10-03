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
        vm.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(vm.SelectedCapture) && vm.SelectedCapture != null) Timeline.ScrollIntoView(vm.SelectedCapture); };
    }
}

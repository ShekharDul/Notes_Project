using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Clips.Core;
using Clips.Infrastructure;

namespace Clips.App;

public sealed record NotebookInput(string Name, string Color, string? Icon);
public static class Dialogs
{
    private static Window Window(string title, double width = 480) => new()
    {
        Title = title, Width = width, SizeToContent = SizeToContent.Height, ResizeMode = ResizeMode.NoResize,
        WindowStartupLocation = WindowStartupLocation.CenterScreen, ShowInTaskbar = false
    };
    private static TextBlock Label(string text) => new() { Text = text, Margin = new Thickness(3, 14, 3, 5), TextWrapping = TextWrapping.Wrap };
    public static bool Confirm(string title, string description, string accept = "Confirm", string cancel = "Cancel")
    {
        var window = Window(title); var panel = new StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(new TextBlock { Text = title, FontSize = 20, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(Label(description)); var buttons = new WrapPanel { Margin = new Thickness(0, 18, 0, 0), HorizontalAlignment = HorizontalAlignment.Right };
        var yes = new Button { Content = accept, IsDefault = true }; yes.Click += (_, _) => window.DialogResult = true;
        buttons.Children.Add(new Button { Content = cancel, IsCancel = true }); buttons.Children.Add(yes); panel.Children.Add(buttons); window.Content = panel;
        return window.ShowDialog() == true;
    }
    public static NotebookInput? EditNotebook(Notebook? notebook)
    {
        var window = Window(notebook == null ? "Create notebook" : "Edit notebook");
        var panel = new StackPanel { Margin = new Thickness(28) };
        panel.Children.Add(new TextBlock { Text = notebook == null ? "A home for your captures" : "Make it yours", FontSize = 24, FontWeight = FontWeights.SemiBold });
        panel.Children.Add(Label("Notebook name")); var name = new TextBox { Text = notebook?.Name ?? "", MaxLength = 101 }; panel.Children.Add(name);
        panel.Children.Add(Label("Cover colour")); var colors = new WrapPanel(); string color = notebook?.CoverColor ?? Product.CoverColors[0];
        foreach (var hex in Product.CoverColors)
        {
            var radio = new RadioButton { Width = 52, Height = 40, Margin = new Thickness(3), GroupName = "cover", IsChecked = color == hex, ToolTip = hex, Content = new Border { Background = (Brush)new BrushConverter().ConvertFromString(hex)!, Width = 27, Height = 27, CornerRadius = new CornerRadius(5) } };
            radio.Checked += (_, _) => color = hex; colors.Children.Add(radio);
        }
        panel.Children.Add(colors); panel.Children.Add(Label("Optional icon"));
        var icon = new ComboBox { ItemsSource = Product.Icons.Select(i => i == "" ? "None" : i), SelectedIndex = Array.IndexOf(Product.Icons, notebook?.Icon ?? "") }; panel.Children.Add(icon);
        var error = Label(""); panel.Children.Add(error);
        var buttons = new WrapPanel { HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 16, 0, 0) };
        buttons.Children.Add(new Button { Content = "Cancel", IsCancel = true });
        var create = new Button { Content = notebook == null ? "Create notebook" : "Save changes", IsDefault = true, Style = (Style)Application.Current.FindResource("Primary") };
        NotebookInput? result = null;
        create.Click += (_, _) =>
        {
            try { result = new(Rules.NotebookName(name.Text), Rules.CoverColor(color), Rules.Icon(icon.SelectedItem?.ToString() == "None" ? null : icon.SelectedItem?.ToString())); window.DialogResult = true; }
            catch (ArgumentException ex) { error.Text = ex.Message; }
        };
        buttons.Children.Add(create); panel.Children.Add(buttons); window.Content = panel; window.Loaded += (_, _) => name.Focus(); window.ShowDialog(); return result;
    }
    public static void ShowImage(string path)
    {
        var window = new Window { Title = "Screenshot", Width = 960, Height = 720, WindowStartupLocation = WindowStartupLocation.CenterScreen };
        var image = new BitmapImage(); image.BeginInit(); image.CacheOption = BitmapCacheOption.OnLoad; image.UriSource = new Uri(path); image.EndInit(); image.Freeze();
        window.Content = new ScrollViewer { Content = new Image { Source = image, Stretch = Stretch.Uniform }, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto };
        window.PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) window.Close(); }; window.Show();
    }
}
public sealed class SettingsWindow : Window
{
    public SettingsWindow(AppSettings settings, AppPaths paths, Func<AppSettings, Task<string>> save)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Title = "Settings"; Width = 570; Height = 740; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(28) }; Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        void Label(string text) => panel.Children.Add(new TextBlock { Text = text, Margin = new Thickness(3, 15, 3, 5), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text = "Settings", FontSize = 27, FontWeight = FontWeights.SemiBold });
        Label("Theme"); var theme = new ComboBox { ItemsSource = Enum.GetValues<AppTheme>(), SelectedItem = settings.Theme }; panel.Children.Add(theme);
        Label("Capture selected text"); var text = new TextBox { Text = settings.TextCaptureShortcut }; panel.Children.Add(text);
        Label("Capture image area (primary display)"); var image = new TextBox { Text = settings.ImageCaptureShortcut }; panel.Children.Add(image);
        Label("Open capture tray"); var tray = new TextBox { Text = settings.OpenTrayShortcut }; panel.Children.Add(tray);
        Label("Use Ctrl+Alt with a letter or F1–F12. Some Windows apps do not expose selected text to accessibility tools. Use image capture for those apps.");
        var notify = new CheckBox { Content = "Enable capture notifications", IsChecked = settings.NotificationsEnabled, Margin = new Thickness(3, 12, 3, 5) }; panel.Children.Add(notify);
        Label("Opening Clips shows your notebook. Start a session to switch to the floating capture panel.");
        Label("Privacy and storage"); Label($"{Product.Name} stores your notebooks, notes, and captures locally on this device. It does not send your content to a server.");
        panel.Children.Add(new TextBox { Text = paths.Root, IsReadOnly = true, TextWrapping = TextWrapping.Wrap });
        var folder = new Button { Content = "Open data folder", HorizontalAlignment = HorizontalAlignment.Left }; folder.Click += (_, _) => System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(paths.Root) { UseShellExecute = true }); panel.Children.Add(folder);
        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(3, 10, 3, 0) }; panel.Children.Add(status);
        var buttons = new WrapPanel { Margin = new Thickness(0, 16, 0, 0) }; var apply = new Button { Content = "Save settings", IsDefault = true, Style = (Style)Application.Current.FindResource("Primary") };
        apply.Click += async (_, _) =>
        {
            apply.IsEnabled = false;
            try { var next = new AppSettings(text.Text.Trim(), image.Text.Trim(), tray.Text.Trim(), notify.IsChecked == true, false, (AppTheme)theme.SelectedItem); Hotkey.Validate(next); status.Text = await save(next); }
            catch (Exception ex) { status.Text = ex is ArgumentException ? ex.Message : "Couldn’t save settings. Check your local storage folder."; }
            finally { apply.IsEnabled = true; }
        };
        buttons.Children.Add(apply); var close = new Button { Content = "Close", IsCancel = true }; close.Click += (_, _) => Close(); buttons.Children.Add(close); panel.Children.Add(buttons);
    }
}

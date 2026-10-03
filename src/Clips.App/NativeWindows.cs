using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using Clips.Core;

namespace Clips.App;

public static class NativeWindows
{
    public static void NonActivating(Window window, bool clickActivates)
    {
        window.ShowActivated = false;
        window.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(window).Handle;
            var style = GetWindowLong(handle, -20) | 0x80;
            // ShowActivated=false prevents focus stealing on Show(). Editable windows must
            // remain activatable afterward; permanently setting NOACTIVATE prevents reliable typing.
            SetWindowLong(handle, -20, clickActivates ? style & ~0x08000000 : style | 0x08000000);
            // MA_ACTIVATE for an intentional mouse click; opening itself remains non-activating.
            HwndSource.FromHwnd(handle)?.AddHook((nint hwnd, int message, nint w, nint l, ref bool handled) =>
            {
                if (message == 0x21) { handled = true; return new nint(clickActivates ? 1 : 3); } return 0;
            });
        };
        if (clickActivates)
        {
            // Explicit user interaction may activate the panel before WPF focuses its editor.
            window.PreviewMouseDown += (_, _) => window.Activate();
            window.PreviewTouchDown += (_, _) => window.Activate();
        }
    }
    public static void PlaceBottomRight(Window window)
    {
        var area = SystemParameters.WorkArea;
        window.Left = area.Right - window.Width - 20; window.Top = area.Bottom - (double.IsNaN(window.Height) ? 200 : window.Height) - 20;
    }
    [DllImport("user32.dll")] private static extern int GetWindowLong(nint handle, int index);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint handle, int index, int value);
}
public sealed class ToastWindow : Window
{
    private readonly System.Windows.Threading.DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(4) };
    public ToastWindow(string message, IEnumerable<(string Label, Action Run)> actions)
    {
        Style = (Style)Application.Current.FindResource(typeof(Window));
        Width = 370; Height = 156; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true; AllowsTransparency = true; Background = Brushes.Transparent;
        NativeWindows.NonActivating(this, true);
        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(18) };
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = Product.Name, FontWeight = FontWeights.SemiBold, FontSize = 12 });
        panel.Children.Add(new System.Windows.Controls.TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 10) });
        var buttons = new System.Windows.Controls.WrapPanel();
        foreach (var (label, run) in actions)
        { var button = new System.Windows.Controls.Button { Content = label, Padding = new Thickness(8, 4, 8, 4) }; button.Click += (_, _) => { Close(); run(); }; buttons.Children.Add(button); }
        panel.Children.Add(buttons);
        var border = new System.Windows.Controls.Border { CornerRadius = new CornerRadius(10), BorderThickness = new Thickness(1), Child = panel }; border.SetResourceReference(System.Windows.Controls.Border.BackgroundProperty, "Card"); border.SetResourceReference(System.Windows.Controls.Border.BorderBrushProperty, "Line"); Content = border;
        NativeWindows.PlaceBottomRight(this); timer.Tick += (_, _) => { if (!IsMouseOver && !IsKeyboardFocusWithin) Close(); };
        Loaded += (_, _) => timer.Start(); Closed += (_, _) => timer.Stop(); PreviewKeyDown += (_, e) => { if (e.Key == System.Windows.Input.Key.Escape) Close(); };
    }
}

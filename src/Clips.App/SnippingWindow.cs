using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Clips.Infrastructure;

namespace Clips.App;
public sealed class SnippingWindow : Window
{
    private readonly Canvas canvas;
    private readonly Rectangle selection;
    private Point? start;
    private readonly ScreenSnapshot snapshot;
    public Int32Rect? Region { get; private set; }
    public SnippingWindow(ScreenSnapshot snapshot)
    {
        this.snapshot = snapshot; Title = "Capture image area"; WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; Topmost = true;
        Left = snapshot.Left / snapshot.DpiScale; Top = snapshot.Top / snapshot.DpiScale; Width = snapshot.Width / snapshot.DpiScale; Height = snapshot.Height / snapshot.DpiScale;
        Cursor = Cursors.Cross;
        var grid = new Grid(); grid.Children.Add(new Image { Source = snapshot.Bitmap, Stretch = Stretch.Fill });
        grid.Children.Add(new Border { Background = new SolidColorBrush(Color.FromArgb(95, 0, 0, 0)) });
        canvas = new Canvas { Background = Brushes.Transparent }; grid.Children.Add(canvas);
        var hint = new TextBlock { Text = "Drag to capture an area · Esc to cancel · Primary display", Foreground = Brushes.White, Background = Brushes.Black, Padding = new Thickness(15), FontSize = 16 }; Canvas.SetLeft(hint, 24); Canvas.SetTop(hint, 24); canvas.Children.Add(hint);
        selection = new Rectangle { Stroke = Brushes.White, StrokeThickness = 2, Fill = new SolidColorBrush(Color.FromArgb(20, 255, 255, 255)), Visibility = Visibility.Hidden }; canvas.Children.Add(selection); Content = grid;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Region = null; DialogResult = false; } };
        canvas.MouseLeftButtonDown += (_, e) => { start = e.GetPosition(canvas); canvas.CaptureMouse(); selection.Visibility = Visibility.Visible; Draw(start.Value); };
        canvas.MouseMove += (_, e) => { if (start != null) Draw(e.GetPosition(canvas)); };
        canvas.MouseLeftButtonUp += (_, e) =>
        {
            if (start == null) return; Draw(e.GetPosition(canvas)); canvas.ReleaseMouseCapture();
            var sx = snapshot.Width / canvas.ActualWidth; var sy = snapshot.Height / canvas.ActualHeight;
            var x = Math.Clamp((int)Math.Floor(Canvas.GetLeft(selection) * sx), 0, snapshot.Width - 1);
            var y = Math.Clamp((int)Math.Floor(Canvas.GetTop(selection) * sy), 0, snapshot.Height - 1);
            var w = Math.Min((int)Math.Round(selection.Width * sx), snapshot.Width - x); var h = Math.Min((int)Math.Round(selection.Height * sy), snapshot.Height - y);
            Region = w >= 8 && h >= 8 ? new Int32Rect(x, y, w, h) : null; DialogResult = Region.HasValue;
        };
        Loaded += (_, _) => Activate();
    }
    private void Draw(Point point)
    {
        if (start == null) return;
        point.X = Math.Clamp(point.X, 0, canvas.ActualWidth); point.Y = Math.Clamp(point.Y, 0, canvas.ActualHeight);
        Canvas.SetLeft(selection, Math.Min(start.Value.X, point.X)); Canvas.SetTop(selection, Math.Min(start.Value.Y, point.Y));
        selection.Width = Math.Abs(point.X - start.Value.X); selection.Height = Math.Abs(point.Y - start.Value.Y);
    }
}
public sealed class SnippingService(ScreenSnapshotService screens)
{
    public async Task<byte[]?> SnipAsync()
    {
        var snapshot = await screens.TakePrimaryAsync();
        var window = new SnippingWindow(snapshot);
        if (window.ShowDialog() != true || window.Region == null) return null;
        return await screens.CropAsync(snapshot, window.Region.Value);
    }
}

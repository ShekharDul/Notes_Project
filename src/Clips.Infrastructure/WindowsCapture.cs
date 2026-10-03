using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Media.Imaging;
using Clips.Core;

namespace Clips.Infrastructure;

public sealed record ForegroundContext(nint Handle, uint ProcessId, SourceContext Source);
public sealed class ForegroundWindowContextService
{
    public ForegroundContext Read()
    {
        var handle = GetForegroundWindow(); GetWindowThreadProcessId(handle, out var processId);
        var title = new StringBuilder(1024); GetWindowText(handle, title, title.Capacity);
        string? name = null;
        try { using var process = Process.GetProcessById((int)processId); name = process.ProcessName; }
        catch (ArgumentException) { }
        catch (System.ComponentModel.Win32Exception) { }
        return new(handle, processId, new(name, title.ToString()));
    }
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hWnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(nint hWnd, StringBuilder text, int count);
}
public sealed class SelectedTextCaptureService(ForegroundWindowContextService foreground) : ISelectedTextReader
{
    // This is a process-name exclusion list, not a content or browsing monitor.
    public ISet<string> ExcludedProcessNames { get; } = new HashSet<string>(["1Password", "KeePass", "KeePassXC", "Bitwarden"], StringComparer.OrdinalIgnoreCase);
    private int busy;
    public async Task<SelectionResult> ReadAsync()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return new(SelectionStatus.UnexpectedError);
        var context = foreground.Read();
        var completion = new TaskCompletionSource<SelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Read(context)); }
            catch (UnauthorizedAccessException) { completion.TrySetResult(new(SelectionStatus.AccessDeniedOrSecureControl)); }
            catch (COMException) { completion.TrySetResult(new(SelectionStatus.UnsupportedApplication)); }
            catch (ElementNotAvailableException) { completion.TrySetResult(new(SelectionStatus.UnsupportedApplication)); }
            catch (Exception) { completion.TrySetResult(new(SelectionStatus.UnexpectedError)); }
            finally { Interlocked.Exchange(ref busy, 0); }
        }) { IsBackground = true, Name = "Explicit selection reader" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        // Hung providers must not hang the UI. Only one provider thread can be pending.
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { return new(SelectionStatus.UnexpectedError); }
    }
    private SelectionResult Read(ForegroundContext context)
    {
        if (context.ProcessId == Environment.ProcessId || ExcludedProcessNames.Contains(context.Source.AppName ?? "")) return new(SelectionStatus.AccessDeniedOrSecureControl);
        var element = AutomationElement.FocusedElement;
        if (element == null) return new(SelectionStatus.NoSelection);
        if (element.Current.ProcessId != context.ProcessId) return new(SelectionStatus.NoSelection);
        if (element.Current.IsPassword) return new(SelectionStatus.AccessDeniedOrSecureControl);
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return new(SelectionStatus.UnsupportedApplication);
        var ranges = ((TextPattern)pattern).GetSelection();
        var text = string.Join("\n", ranges.Select(r => r.GetText(1_000_000)));
        if (string.IsNullOrWhiteSpace(text)) return new(SelectionStatus.NoSelection);
        // Do not attribute a delayed provider result to an app that is no longer foreground.
        if (foreground.Read().Handle != context.Handle) return new(SelectionStatus.NoSelection);
        return new(SelectionStatus.Success, text, context.Source);
    }
}
public sealed record ScreenSnapshot(BitmapSource Bitmap, int Left, int Top, int Width, int Height, double DpiScale);
public sealed class ScreenSnapshotService
{
    public Task<ScreenSnapshot> TakePrimaryAsync() => Task.Run(() =>
    {
        var monitor = MonitorFromPoint(new PointNative(0, 0), 1);
        var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>() };
        if (!GetMonitorInfo(monitor, ref info)) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var rect = info.Monitor; var width = rect.Right - rect.Left; var height = rect.Bottom - rect.Top;
        using var bitmap = new System.Drawing.Bitmap(width, height, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
        using (var graphics = System.Drawing.Graphics.FromImage(bitmap)) graphics.CopyFromScreen(rect.Left, rect.Top, 0, 0, new System.Drawing.Size(width, height), System.Drawing.CopyPixelOperation.SourceCopy);
        using var stream = new MemoryStream(); bitmap.Save(stream, System.Drawing.Imaging.ImageFormat.Png); stream.Position = 0;
        var source = new BitmapImage(); source.BeginInit(); source.CacheOption = BitmapCacheOption.OnLoad; source.StreamSource = stream; source.EndInit(); source.Freeze();
        _ = GetDpiForMonitor(monitor, 0, out var dpiX, out _);
        return new ScreenSnapshot(source, rect.Left, rect.Top, width, height, dpiX > 0 ? dpiX / 96.0 : 1);
    });
    public Task<byte[]> CropAsync(ScreenSnapshot snapshot, System.Windows.Int32Rect rect) => Task.Run(() =>
    {
        var cropped = new CroppedBitmap(snapshot.Bitmap, rect); cropped.Freeze();
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(cropped));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    });
    [StructLayout(LayoutKind.Sequential)] private readonly record struct PointNative(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct RectNative { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct MonitorInfo { public int Size; public RectNative Monitor; public RectNative Work; public uint Flags; }
    [DllImport("user32.dll")] private static extern nint MonitorFromPoint(PointNative point, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern bool GetMonitorInfo(nint monitor, ref MonitorInfo info);
    [DllImport("shcore.dll")] private static extern int GetDpiForMonitor(nint monitor, int type, out uint x, out uint y);
}

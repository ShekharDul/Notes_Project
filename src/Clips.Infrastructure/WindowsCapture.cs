using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using System.Windows.Media.Imaging;
using Clips.Core;

namespace Clips.Infrastructure;

public sealed record ForegroundContext(nint Handle, uint ProcessId, SourceContext Source);
public sealed class ForegroundWindowContextService
{
    public bool IsCurrent(nint handle) => GetForegroundWindow() == handle;
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
public sealed class SelectedTextCaptureService(ForegroundWindowContextService foreground, ILocalLog log) : ISelectedTextReader
{
    // This is a process-name exclusion list, not a content or browsing monitor.
    public ISet<string> ExcludedProcessNames { get; } = new HashSet<string>(["1Password", "KeePass", "KeePassXC", "Bitwarden"], StringComparer.OrdinalIgnoreCase);
    private int busy;
    public async Task<SelectionResult> ReadAsync()
    {
        if (Interlocked.CompareExchange(ref busy, 1, 0) != 0) return new(SelectionStatus.TimedOut);
        var context = foreground.Read();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        var cancellation = timeout.Token;
        var completion = new TaskCompletionSource<SelectionResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(Read(context, cancellation)); }
            catch (UnauthorizedAccessException) { completion.TrySetResult(new(SelectionStatus.AccessDeniedOrSecureControl)); }
            catch (COMException ex) { log.Event("selection.com.failed", ex.HResult); completion.TrySetResult(new(SelectionStatus.ProviderError)); }
            catch (ElementNotAvailableException ex) { log.Event("selection.element.unavailable", ex.HResult); completion.TrySetResult(new(SelectionStatus.ProviderError)); }
            catch (OperationCanceledException) { completion.TrySetResult(new(SelectionStatus.TimedOut)); }
            catch (Exception ex) { log.Event("selection.unexpected", ex.HResult); completion.TrySetResult(new(SelectionStatus.UnexpectedError)); }
            finally { Interlocked.Exchange(ref busy, 0); }
        }) { IsBackground = true, Name = "Explicit selection reader" };
        thread.SetApartmentState(ApartmentState.STA); thread.Start();
        // Hung providers must not hang the UI. Only one provider thread can be pending.
        try { return await completion.Task.WaitAsync(TimeSpan.FromSeconds(2)); }
        catch (TimeoutException) { timeout.Cancel(); log.Event("selection.timeout"); return new(SelectionStatus.TimedOut); }
    }
    private SelectionResult Read(ForegroundContext context, CancellationToken cancellation)
    {
        if (context.ProcessId == Environment.ProcessId || ExcludedProcessNames.Contains(context.Source.AppName ?? "")) return new(SelectionStatus.AccessDeniedOrSecureControl);
        cancellation.ThrowIfCancellationRequested();
        var root = AutomationElement.FromHandle(context.Handle);
        var focused = AutomationElement.FocusedElement;
        var result = new SelectionDiscovery(log).Read(new AutomationSelectionNode(root), focused == null ? null : new AutomationSelectionNode(focused),
            () => foreground.IsCurrent(context.Handle), cancellation);
        log.Event(result.Status switch
        {
            SelectionStatus.Success => "selection.success", SelectionStatus.NoSelection => "selection.none",
            SelectionStatus.TimedOut => "selection.timeout", SelectionStatus.ProviderError => "selection.provider_error",
            SelectionStatus.ForegroundChanged => "selection.foreground_changed", SelectionStatus.SelectionTooLarge => "selection.too_large",
            SelectionStatus.AccessDeniedOrSecureControl => "selection.secure", _ => "selection.unsupported"
        });
        return result.Status == SelectionStatus.Success ? result with { Source = context.Source } : result;
    }
}
internal sealed class AutomationSelectionNode(AutomationElement element) : ISelectionNode
{
    private static readonly TreeWalker Walker = TreeWalker.RawViewWalker;
    public string Identity => string.Join(",", element.GetRuntimeId());
    public bool IsPassword => element.Current.IsPassword;
    public bool IsOffscreen => element.Current.IsOffscreen;
    public ISelectionNode? Parent => Walker.GetParent(element) is { } parent ? new AutomationSelectionNode(parent) : null;
    public IEnumerable<ISelectionNode> Children
    {
        get
        {
            for (var child = Walker.GetFirstChild(element); child != null; child = Walker.GetNextSibling(child)) yield return new AutomationSelectionNode(child);
        }
    }
    public string? ReadSelectedText(int maximumCharacters)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var pattern)) return null;
        var builder = new StringBuilder();
        foreach (var range in ((TextPattern)pattern).GetSelection())
        {
            // A caret is an empty range, not permission to read the enclosing document.
            if (range.CompareEndpoints(TextPatternRangeEndpoint.Start, range, TextPatternRangeEndpoint.End) == 0) continue;
            var remaining = maximumCharacters + 1 - builder.Length;
            if (remaining <= 0) break;
            if (builder.Length > 0) { builder.Append('\n'); remaining--; }
            if (remaining > 0) builder.Append(range.GetText(remaining));
        }
        return builder.ToString();
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

using System.Runtime.InteropServices;
using System.Windows.Input;
using System.Windows.Interop;
using Clips.Core;

namespace Clips.Infrastructure;

public sealed record Hotkey(uint Modifiers, uint VirtualKey, string Label)
{
    public static Hotkey Parse(string text)
    {
        var parts = text.Split('+', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        uint modifiers = 0;
        if (parts.Length < 2) throw new ArgumentException("Use Ctrl+Alt and a letter or function key.");
        foreach (var part in parts[..^1]) modifiers |= part.ToUpperInvariant() switch
        { "CTRL" or "CONTROL" => 2u, "ALT" => 1u, "SHIFT" => 4u, _ => throw new ArgumentException("Use Ctrl, Alt or Shift modifiers.") };
        if ((modifiers & 3) != 3) throw new ArgumentException("Capture shortcuts must include Ctrl+Alt.");
        if (!Enum.TryParse<Key>(parts[^1], true, out var key) || !(key >= Key.A && key <= Key.Z || key >= Key.F1 && key <= Key.F12))
            throw new ArgumentException("Choose a letter A–Z or function key F1–F12.");
        return new(modifiers, (uint)KeyInterop.VirtualKeyFromKey(key), text);
    }
    public static Hotkey[] Validate(AppSettings settings)
    {
        var keys = new[] { Parse(settings.TextCaptureShortcut), Parse(settings.ImageCaptureShortcut), Parse(settings.OpenTrayShortcut) };
        if (keys.Select(k => (k.Modifiers, k.VirtualKey)).Distinct().Count() != keys.Length) throw new ArgumentException("Each shortcut must be different.");
        return keys;
    }
}
public sealed class GlobalHotkeyService : IDisposable
{
    private readonly HwndSource source;
    private readonly HashSet<int> registered = [];
    public event Action<int>? Pressed;
    public GlobalHotkeyService()
    {
        source = new HwndSource(new HwndSourceParameters("Clips hotkey receiver") { ParentWindow = new nint(-3), Width = 0, Height = 0, WindowStyle = 0 });
        source.AddHook(Hook);
    }
    public IReadOnlyList<string> Apply(AppSettings settings, bool active)
    {
        var keys = Hotkey.Validate(settings); Clear(); var failed = new List<string>();
        for (var i = 0; i < keys.Length; i++)
        {
            if (i < 2 && !active) continue;
            var key = keys[i];
            if (RegisterHotKey(source.Handle, i + 1, key.Modifiers | 0x4000, key.VirtualKey)) registered.Add(i + 1);
            else failed.Add($"{key.Label} is unavailable. Windows or another app owns it. Change it in Settings.");
        }
        return failed;
    }
    private nint Hook(nint hwnd, int message, nint wParam, nint lParam, ref bool handled)
    {
        if (message == 0x312) { handled = true; Pressed?.Invoke((int)wParam); } return 0;
    }
    private void Clear() { foreach (var id in registered) UnregisterHotKey(source.Handle, id); registered.Clear(); }
    public void Dispose() { Clear(); source.RemoveHook(Hook); source.Dispose(); }
    [DllImport("user32.dll", SetLastError = true)] private static extern bool RegisterHotKey(nint hWnd, int id, uint modifiers, uint virtualKey);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(nint hWnd, int id);
}

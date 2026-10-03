using System.Globalization;
using System.Text.RegularExpressions;

namespace Clips.Core;

public static class Product
{
    public const string Name = "Clips";
    public const string StorageDirectoryName = "Clips";
    public static readonly string[] CoverColors = ["#6B7BFF", "#A16BD6", "#2B897F", "#CF7460", "#B28A35", "#577FA5"];
    public static readonly string[] Icons = ["", "📚", "🌿", "🔬", "💡", "🎨", "📌"];
}
public enum SessionStatus { Active, Paused, Ended }
public enum CaptureType { Text, Image }
public enum AppTheme { System, Light, Dark }
public sealed record Notebook(Guid Id, string Name, string CoverColor, string? Icon, DateTime CreatedAtUtc, DateTime UpdatedAtUtc, bool IsArchived = false);
public sealed record CaptureSession(Guid Id, Guid NotebookId, DateTime StartedAtUtc, DateTime? EndedAtUtc, SessionStatus Status, DateTime CreatedAtUtc);
public sealed record SourceContext(string? AppName = null, string? WindowTitle = null);
public sealed record CaptureItem(Guid Id, Guid NotebookId, Guid SessionId, CaptureType Type, string? ContentText, string? ImageRelativePath, string? UserNote, string? SourceAppName, string? SourceWindowTitle, string? SourceUrl, string? SourceDocumentName, string? SourcePageHint, DateTime CapturedAtUtc, int DisplayOrder, DateTime CreatedAtUtc, DateTime UpdatedAtUtc);
public sealed record AppSettings(string TextCaptureShortcut = "Ctrl+Alt+S", string ImageCaptureShortcut = "Ctrl+Alt+A", string OpenTrayShortcut = "Ctrl+Alt+N", bool NotificationsEnabled = true, bool StartMinimizedToTray = false, AppTheme Theme = AppTheme.System);
public enum SelectionStatus { Success, NoSelection, UnsupportedApplication, AccessDeniedOrSecureControl, UnexpectedError }
public sealed record SelectionResult(SelectionStatus Status, string? Text = null, SourceContext? Source = null);
public sealed record CaptureOutcome(CaptureItem? Item, string Message, bool OfferScreenshot = false, bool OpenApp = false);

public static partial class Rules
{
    public static string NotebookName(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 100) throw new ArgumentException("Enter a notebook name between 1 and 100 characters.");
        return name;
    }
    public static string CoverColor(string color)
    {
        if (!HexColor().IsMatch(color)) throw new ArgumentException("Choose a valid cover colour (#RRGGBB).");
        return color.ToUpperInvariant();
    }
    public static string? Icon(string? icon)
    {
        if (string.IsNullOrWhiteSpace(icon)) return null;
        if (!Product.Icons.Contains(icon) && StringInfo.ParseCombiningCharacters(icon).Length != 1)
            throw new ArgumentException("Choose one emoji or a curated icon.");
        return icon;
    }
    public static string? Note(string? note)
    {
        if (note?.Length > 10_000) throw new ArgumentException("Notes can contain up to 10,000 characters.");
        return string.IsNullOrEmpty(note) ? null : note;
    }
    public static string NormalizeText(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
        return string.Join("\n", lines.Select(l => HorizontalWhitespace().Replace(l, " ").Trim())).Trim();
    }
    public static CaptureOutcome SelectionFallback(SelectionStatus status) => status switch
    {
        SelectionStatus.NoSelection => new(null, "No selected text found. Select text, then try again.", true, true),
        SelectionStatus.AccessDeniedOrSecureControl => new(null, "Capture is unavailable for this secure or excluded app.", false, true),
        _ => new(null, "We couldn’t read selected text from this app.", true, true)
    };
    [GeneratedRegex("^#[0-9a-fA-F]{6}$")] private static partial Regex HexColor();
    [GeneratedRegex(@"[^\S\r\n]+")] private static partial Regex HorizontalWhitespace();
}

public sealed class SessionConflictException(Guid notebookId) : InvalidOperationException("Another notebook has an open capture session.")
{
    public Guid NotebookId { get; } = notebookId;
}

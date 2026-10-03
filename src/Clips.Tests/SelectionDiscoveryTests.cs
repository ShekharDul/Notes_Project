using System.Runtime.InteropServices;
using Clips.Core;
using Clips.Infrastructure;
using Xunit;

namespace Clips.Tests;
public sealed class SelectionDiscoveryTests
{
    private sealed class Node(string identity, string? selection = null) : ISelectionNode
    {
        public string Identity { get; } = identity;
        public bool IsPassword { get; set; }
        public bool IsOffscreen { get; set; }
        public ISelectionNode? Parent { get; set; }
        public List<Node> Nodes { get; } = [];
        public IEnumerable<ISelectionNode> Children => Nodes;
        public int ReadCalls { get; private set; }
        public Func<int, string?>? Read { get; set; }
        public string? ReadSelectedText(int maximumCharacters) { ReadCalls++; return Read == null ? selection : Read(maximumCharacters); }
        public Node Add(Node child) { child.Parent = this; Nodes.Add(child); return child; }
    }
    private sealed class Events : ILocalLog
    {
        public List<string> Names { get; } = [];
        public void Event(string eventName, int? errorCode = null) => Names.Add(eventName);
    }
    private static SelectionResult Discover(Node root, Node? focus, Func<bool>? current = null, CancellationToken cancellation = default) =>
        new SelectionDiscovery(new NullLog()).Read(root, focus, current ?? (() => true), cancellation);

    [Fact] public void FocusedSelectionWinsOverOtherDocumentSelection()
    {
        var root = new Node("window"); var focus = root.Add(new Node("focus", "Explicit selection")); var other = root.Add(new Node("other", "Other text"));
        Assert.Equal("Explicit selection", Discover(root, focus).Text); Assert.Equal(0, other.ReadCalls);
    }
    [Fact] public void FocusedLeafCanUseContainingDocumentSelection()
    {
        var root = new Node("window"); var document = root.Add(new Node("pdf-document", "Selected PDF paragraph")); var leaf = document.Add(new Node("focus-leaf"));
        Assert.Equal("Selected PDF paragraph", Discover(root, leaf).Text);
    }
    [Fact] public void UnsupportedFocusCanDiscoverPdfProviderElsewhereInWindow()
    {
        var root = new Node("window"); var focus = root.Add(new Node("viewer-container"));
        var pane = root.Add(new Node("content-pane")); var document = pane.Add(new Node("pdf-document", "Figure description\n\nSecond paragraph"));
        var result = Discover(root, focus); Assert.Equal(SelectionStatus.Success, result.Status); Assert.Equal("Figure description\n\nSecond paragraph", result.Text); Assert.Equal(1, document.ReadCalls);
    }
    [Fact] public void MissingFocusedElementCanStillDiscoverForegroundDocument()
    {
        var root = new Node("window"); root.Add(new Node("document", "Selected text")); Assert.Equal(SelectionStatus.Success, Discover(root, null).Status);
    }
    [Fact] public void FocusOutsideWindowIsRejectedBeforeAnyTextRead()
    {
        var root = new Node("window", "Do not capture"); var otherRoot = new Node("other-window"); var focus = otherRoot.Add(new Node("focus", "Wrong window"));
        Assert.Equal(SelectionStatus.ForegroundChanged, Discover(root, focus).Status); Assert.Equal(0, root.ReadCalls); Assert.Equal(0, focus.ReadCalls);
    }
    [Theory][InlineData(true)][InlineData(false)]
    public void SecureFocusOrAncestorRejectsEntireCapture(bool focusIsSecure)
    {
        var root = new Node("window", "Unrelated text"); var secure = root.Add(new Node("secure-parent") { IsPassword = !focusIsSecure });
        var focus = secure.Add(new Node("focus", "Secret") { IsPassword = focusIsSecure });
        Assert.Equal(SelectionStatus.AccessDeniedOrSecureControl, Discover(root, focus).Status); Assert.Equal(0, focus.ReadCalls); Assert.Equal(0, root.ReadCalls);
    }
    [Fact] public void OffscreenAndPasswordBranchesAreNeverRead()
    {
        var root = new Node("window"); var focus = root.Add(new Node("focus"));
        var hidden = root.Add(new Node("inactive-tab", "Old selection") { IsOffscreen = true }); var hiddenChild = hidden.Add(new Node("hidden-doc", "Secret"));
        var secure = root.Add(new Node("password", "Secret") { IsPassword = true }); var visible = root.Add(new Node("current-doc", "Visible selection"));
        Assert.Equal("Visible selection", Discover(root, focus).Text); Assert.Equal(0, hidden.ReadCalls); Assert.Equal(0, hiddenChild.ReadCalls); Assert.Equal(0, secure.ReadCalls); Assert.Equal(1, visible.ReadCalls);
    }
    [Fact] public void EmptyCaretDoesNotBecomeWholeDocumentCapture()
    {
        var root = new Node("window"); var focus = root.Add(new Node("edit", ""));
        var result = Discover(root, focus); Assert.Equal(SelectionStatus.NoSelection, result.Status); Assert.Null(result.Text);
    }
    [Fact] public void ProviderFailureCanRecoverThroughAnotherProviderAndLogsOnlyEventName()
    {
        var events = new Events(); var root = new Node("window"); var focus = root.Add(new Node("focus") { Read = _ => throw new COMException("Private document name", unchecked((int)0x80004005)) });
        root.Add(new Node("document", "Selected text")); var result = new SelectionDiscovery(events).Read(root, focus, () => true, default);
        Assert.Equal(SelectionStatus.Success, result.Status); Assert.Equal(new[] { "selection.provider.failed" }, events.Names);
    }
    [Fact] public void UnrecoverableProviderFailureIsDistinctFromUnsupportedApp()
    {
        var root = new Node("window"); var focus = root.Add(new Node("focus") { Read = _ => throw new COMException() });
        Assert.Equal(SelectionStatus.ProviderError, Discover(root, focus).Status); Assert.Equal(SelectionStatus.UnsupportedApplication, Discover(new Node("empty"), null).Status);
    }
    [Fact] public void CancellationDoesNotReadAnySelection()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel(); var root = new Node("window", "Selection");
        Assert.Equal(SelectionStatus.TimedOut, Discover(root, root, cancellation: cancellation.Token).Status); Assert.Equal(0, root.ReadCalls);
    }
    [Fact] public void ForegroundChangeDuringRetrievalDiscardsSelectedText()
    {
        var current = true; var root = new Node("window"); var focus = root.Add(new Node("focus") { Read = _ => { current = false; return "Do not save"; } });
        var result = Discover(root, focus, () => current); Assert.Equal(SelectionStatus.ForegroundChanged, result.Status); Assert.Null(result.Text);
    }
    [Fact] public void TraversalHasNodeAndDepthLimits()
    {
        var root = new Node("window"); for (var i = 0; i < 30; i++) root.Add(new Node("node" + i));
        var events = new Events(); new SelectionDiscovery(events, maximumNodes: 8).Read(root, null, () => true, default);
        Assert.Equal(8, root.ReadCalls + root.Nodes.Sum(n => n.ReadCalls)); Assert.Contains("selection.search.limit", events.Names);
        var deepRoot = new Node("window"); var deep = deepRoot.Add(new Node("one")).Add(new Node("two")).Add(new Node("three", "Beyond bound"));
        Assert.Equal(SelectionStatus.UnsupportedApplication, new SelectionDiscovery(new NullLog(), maximumDepth: 2).Read(deepRoot, null, () => true, default).Status); Assert.Equal(0, deep.ReadCalls);
    }
    [Fact] public void OversizedSelectionIsRejectedWithoutSavingTruncatedText()
    {
        var root = new Node("window") { Read = max => new string('x', max + 1) };
        var result = Discover(root, root); Assert.Equal(SelectionStatus.SelectionTooLarge, result.Status); Assert.Null(result.Text);
    }
    [Theory][InlineData(SelectionStatus.TimedOut)][InlineData(SelectionStatus.ProviderError)][InlineData(SelectionStatus.ForegroundChanged)][InlineData(SelectionStatus.SelectionTooLarge)]
    public void NewFailureReasonsHaveActionableMessages(SelectionStatus status)
    {
        var fallback = Rules.SelectionFallback(status); Assert.Null(fallback.Item); Assert.True(fallback.OpenApp); Assert.NotEqual(Rules.SelectionFallback(SelectionStatus.UnsupportedApplication).Message, fallback.Message);
    }
}

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Automation;
using Clips.Core;

namespace Clips.Infrastructure;

// Small adapter boundary lets us test provider discovery without reading any user applications.
public interface ISelectionNode
{
    string Identity { get; }
    bool IsPassword { get; }
    bool IsOffscreen { get; }
    ISelectionNode? Parent { get; }
    IEnumerable<ISelectionNode> Children { get; }
    // null means no TextPattern, empty means a TextPattern with no nonempty selection.
    string? ReadSelectedText(int maximumCharacters);
}

public sealed class SelectionDiscovery(ILocalLog log, int maximumNodes = 256, int maximumDepth = 16)
{
    public const int MaximumCharacters = 1_000_000;
    public SelectionResult Read(ISelectionNode root, ISelectionNode? focused, Func<bool> isForeground, CancellationToken cancellation)
    {
        var seen = new HashSet<string>();
        var providers = false;
        var providerError = false;
        var watch = Stopwatch.StartNew();
        void Check()
        {
            cancellation.ThrowIfCancellationRequested();
            if (watch.Elapsed > TimeSpan.FromMilliseconds(1800)) throw new OperationCanceledException();
        }
        SelectionResult? Inspect(ISelectionNode node)
        {
            Check();
            if (!seen.Add(node.Identity)) return null;
            if (node.IsPassword || node.IsOffscreen) return null;
            try
            {
                var selected = node.ReadSelectedText(MaximumCharacters);
                Check();
                if (selected == null) return null;
                providers = true;
                if (selected.Length > MaximumCharacters) return new(SelectionStatus.SelectionTooLarge);
                if (string.IsNullOrWhiteSpace(selected)) return null;
                return isForeground() ? new(SelectionStatus.Success, selected) : new(SelectionStatus.ForegroundChanged);
            }
            catch (COMException ex) { providerError = true; log.Event("selection.provider.failed", ex.HResult); return null; }
            catch (ElementNotAvailableException ex) { providerError = true; log.Event("selection.provider.stale", ex.HResult); return null; }
            catch (InvalidOperationException ex) { providerError = true; log.Event("selection.provider.unavailable", ex.HResult); return null; }
        }
        try
        {
            Check();
            if (!isForeground()) return new(SelectionStatus.ForegroundChanged);
            var rootId = root.Identity;
            // Validate window membership by ancestry, not process ID: Chromium uses multiple processes.
            var ancestry = new List<ISelectionNode>();
            if (focused != null)
            {
                var ancestorIds = new HashSet<string>();
                for (var node = focused; node != null && ancestry.Count <= maximumDepth * 2; node = node.Parent)
                {
                    Check();
                    if (!ancestorIds.Add(node.Identity)) break;
                    if (node.IsPassword) return new(SelectionStatus.AccessDeniedOrSecureControl);
                    ancestry.Add(node);
                    if (node.Identity == rootId) break;
                }
                if (ancestry.Count == 0 || ancestry[^1].Identity != rootId) return new(SelectionStatus.ForegroundChanged);
                foreach (var node in ancestry) if (Inspect(node) is { } result) return result;
            }
            // Breadth-first traversal reaches the document/container before its many text children.
            // Enumerate incrementally; never FindAll() an unbounded browser tree or read DocumentRange.
            var queue = new Queue<(ISelectionNode Node, int Depth)>(); queue.Enqueue((root, 0));
            var visited = new HashSet<string>(); var scheduled = 1;
            while (queue.TryDequeue(out var entry) && visited.Count < maximumNodes)
            {
                Check();
                if (!isForeground()) return new(SelectionStatus.ForegroundChanged);
                var node = entry.Node;
                try
                {
                    if (!visited.Add(node.Identity) || node.IsPassword || node.IsOffscreen) continue;
                    if (Inspect(node) is { } result) return result;
                    if (entry.Depth >= maximumDepth) continue;
                    foreach (var child in node.Children)
                    {
                        Check(); if (scheduled >= maximumNodes) break;
                        queue.Enqueue((child, entry.Depth + 1)); scheduled++;
                    }
                }
                catch (COMException ex) { providerError = true; log.Event("selection.tree.failed", ex.HResult); }
                catch (ElementNotAvailableException ex) { providerError = true; log.Event("selection.tree.stale", ex.HResult); }
                catch (InvalidOperationException ex) { providerError = true; log.Event("selection.tree.unavailable", ex.HResult); }
            }
            if (scheduled >= maximumNodes) log.Event("selection.search.limit");
            Check();
            if (!isForeground()) return new(SelectionStatus.ForegroundChanged);
            return new(providerError ? SelectionStatus.ProviderError : providers ? SelectionStatus.NoSelection : SelectionStatus.UnsupportedApplication);
        }
        catch (OperationCanceledException) { return new(SelectionStatus.TimedOut); }
        catch (UnauthorizedAccessException) { return new(SelectionStatus.AccessDeniedOrSecureControl); }
        catch (COMException ex) { log.Event("selection.discovery.failed", ex.HResult); return new(SelectionStatus.ProviderError); }
        catch (ElementNotAvailableException ex) { log.Event("selection.discovery.stale", ex.HResult); return new(SelectionStatus.ProviderError); }
        catch (InvalidOperationException ex) { log.Event("selection.discovery.unavailable", ex.HResult); return new(SelectionStatus.ProviderError); }
    }
}

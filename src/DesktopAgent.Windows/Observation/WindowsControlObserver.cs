using System.Collections.Immutable;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using DesktopAgent.Core.Domain;
using DesktopAgent.Core.Ports;
using DesktopAgent.Core.Runtime;

namespace DesktopAgent.Windows.Observation;

/// <summary>Read-only UIA client. One bounded MTA worker, no Invoke/SetValue/Select/Focus calls.</summary>
internal sealed class WindowsControlObserver(IDesktopObserver desktop, Action<object>? resolutionTrace = null) : IControlObserver, IDisposable
{
    private sealed record ReadResult(ControlSnapshot Snapshot, Dictionary<string, int[]> RuntimeIds, HostedWindowIdentity Identity,
        DesktopBackgroundLocator.Binding? DesktopBackground, Dictionary<string, ShellMenuLocator.Binding> MenuBindings,
        NativeShellRenameEdit.Binding? RenameEdit);
    private readonly BoundedReadWorker _worker = new("Desktop Agent read-only UIA");
    private readonly SemaphoreSlim _serial = new(1, 1);
    private ReadResult? _current;
    public bool Enabled { get; set; } = true;

    public async Task<ControlSnapshot> ObserveAsync(Frame frame, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            _current = null;
            if (!Enabled) return ControlSnapshot.Empty(frame, ControlSnapshotStatus.Disabled);
            if (FrameChecks.Validate(frame, frame.Lease, await desktop.GetEnvironmentAsync(ct), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90)) is not null)
                return ControlSnapshot.Empty(frame, ControlSnapshotStatus.Unavailable);
            var read = await _worker.ReadAsync(() => Read(frame), TimeSpan.FromMilliseconds(1250), ct);
            ct.ThrowIfCancellationRequested();
            if (read.Status != ReadWorkerStatus.Completed || read.Value is null)
                return ControlSnapshot.Empty(frame, read.Status == ReadWorkerStatus.TimedOut ? ControlSnapshotStatus.TimedOut : ControlSnapshotStatus.Unavailable);
            if (FrameChecks.Validate(frame, frame.Lease, await desktop.GetEnvironmentAsync(ct), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90)) is not null)
                return ControlSnapshot.Empty(frame, ControlSnapshotStatus.Unavailable);
            if (!read.Value.Identity.IsCurrent()) return ControlSnapshot.Empty(frame, ControlSnapshotStatus.Unavailable);
            ct.ThrowIfCancellationRequested();
            read.Value.Snapshot.Validate(frame);
            _current = read.Value;
            return read.Value.Snapshot;
        }
        finally { _serial.Release(); }
    }

    public async Task<ControlResolution> ResolveAsync(Frame frame, ControlSnapshot snapshot, string controlId, CancellationToken ct)
    {
        await _serial.WaitAsync(ct);
        try
        {
            if (!Enabled || _current is null || !ReferenceEquals(_current.Snapshot, snapshot) || snapshot.FrameId != frame.Id || snapshot.Lease != frame.Lease)
                return new(null, "STALE_CONTROL_REFERENCE");
            bool desktopBackground = controlId == DesktopBackgroundLocator.CandidateId && _current.DesktopBackground is not null;
            bool nativeRename = controlId == NativeShellRenameEdit.CandidateId && _current.RenameEdit is not null;
            if (!_current.RuntimeIds.TryGetValue(controlId, out var runtimeId) && !desktopBackground && !nativeRename) return new(null, "STALE_CONTROL_REFERENCE");
            var candidate = snapshot.Candidates.Single(c => c.Id == controlId);
            if (!candidate.Enabled) return new(null, "CONTROL_DISABLED");
            string? stale = FrameChecks.Validate(frame, snapshot.Lease, await desktop.GetEnvironmentAsync(ct), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90));
            if (stale is not null) return new(null, stale);
            var identity = _current.Identity;
            var backgroundBinding = _current.DesktopBackground;
            var renameBinding = _current.RenameEdit;
            _current.MenuBindings.TryGetValue(controlId, out var menuBinding);
            if (!identity.IsCurrent()) return new(null, "CONTROL_HOST_CHANGED");
            var result = await _worker.ReadAsync(() =>
            {
                try { return desktopBackground
                    ? DesktopBackgroundLocator.Resolve(frame, candidate, backgroundBinding!, identity)
                    : nativeRename ? NativeShellRenameEdit.Resolve(frame, candidate, renameBinding!, identity)
                    : menuBinding is not null ? ShellMenuLocator.Resolve(frame, candidate, runtimeId!, menuBinding, identity, Cache(), Candidate)
                    : Resolve(frame, candidate, runtimeId!, identity, resolutionTrace); }
                catch (Exception error)
                {
                    resolutionTrace?.Invoke(new { stage = "resolve_exception", errorType = error.GetType().Name, error.HResult, error.StackTrace });
                    throw;
                }
            }, TimeSpan.FromMilliseconds(750), ct);
            ct.ThrowIfCancellationRequested();
            stale = FrameChecks.Validate(frame, snapshot.Lease, await desktop.GetEnvironmentAsync(ct), DateTimeOffset.UtcNow, TimeSpan.FromSeconds(90));
            if (stale is not null) return new(null, stale);
            if (!identity.IsCurrent()) return new(null, "CONTROL_HOST_CHANGED");
            ct.ThrowIfCancellationRequested();
            return result.Status == ReadWorkerStatus.Completed && result.Value is not null ? result.Value : new(null, "CONTROL_RECHECK_UNAVAILABLE");
        }
        finally { _serial.Release(); }
    }

    private static CacheRequest Cache()
    {
        var cache = new CacheRequest { TreeScope = TreeScope.Element, AutomationElementMode = AutomationElementMode.Full };
        foreach (var property in new[] { AutomationElement.NameProperty, AutomationElement.ControlTypeProperty,
            AutomationElement.IsPasswordProperty, AutomationElement.IsEnabledProperty, AutomationElement.IsOffscreenProperty,
            AutomationElement.BoundingRectangleProperty, AutomationElement.ProcessIdProperty, AutomationElement.NativeWindowHandleProperty,
            AutomationElement.HasKeyboardFocusProperty, AutomationElement.IsKeyboardFocusableProperty,
            TogglePattern.ToggleStateProperty, SelectionItemPattern.IsSelectedProperty,
            ExpandCollapsePattern.ExpandCollapseStateProperty }) cache.Add(property);
        return cache;
    }

    private static ReadResult Read(Frame frame)
    {
        using var dpi = new PhysicalDpiScope();
        var clock = Stopwatch.StartNew();
        var cache = Cache();
        // Fetch each immediate child group in one UIA call, not one cross-process
        // call per sibling. Do not request an unbounded whole-window subtree.
        cache.TreeScope = TreeScope.Element | TreeScope.Children;
        var identity = HostedWindowIdentity.TryCapture(frame.Foreground) ?? throw new InvalidOperationException("CONTROL_HOST_UNAVAILABLE");
        // The real desktop filename Edit can be exposed as UIA Document with no text patterns.
        // Capture the narrow native keyboard receiver first so a long icon tree cannot hide it.
        var rename = NativeShellRenameEdit.TryCapture(frame, identity);
        var desktopBackground = DesktopBackgroundLocator.TryCapture(frame, identity);
        var candidates = ImmutableArray.CreateBuilder<ControlCandidate>();
        var references = new Dictionary<string, int[]>(StringComparer.Ordinal);
        var menuBindings = new Dictionary<string, ShellMenuLocator.Binding>(StringComparer.Ordinal);
        if (rename is not null) candidates.Add(rename.Candidate);
        if (desktopBackground is not null) candidates.Add(DesktopBackgroundLocator.Candidate(desktopBackground));
        clock.Restart();
        var menuRoots = ShellMenuLocator.ReadRoots(identity, cache);
        ReadResult Result(bool partial) => new(new(Guid.NewGuid().ToString("N"), frame.Lease, frame.Id, DateTimeOffset.UtcNow,
            partial ? ControlSnapshotStatus.Partial : ControlSnapshotStatus.Available, candidates.ToImmutable()), references, identity, desktopBackground, menuBindings, rename?.Binding);
        AutomationElement? root = null;
        try
        {
            root = AutomationElement.FromHandle(identity.HostHwnd).GetUpdatedCache(cache);
            if (!IsHostRoot(root, identity)) throw new InvalidOperationException("CONTROL_HOST_ROOT_MISMATCH");
        }
        catch when ((desktopBackground is not null || menuRoots.Count > 0 || rename is not null) && identity.IsCurrent()) { root = null; }
        if (menuRoots.Count == 0) clock.Restart(); // Preserve ordinary-window cold UIA initialization allowance.
        // Popup roots and ordinary host controls share the same traversal and candidate budget.
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (root is not null) seen.Add(string.Join(",", root.GetRuntimeId()));
        foreach (var menu in menuRoots) seen.Add(string.Join(",", menu.Element.GetRuntimeId()));
        int visited = 0, characters = candidates.Sum(c => c.Name.Length + (c.CurrentValue?.Length ?? 0));
        bool partial = root is null;
        bool Limited() => visited >= 384 || candidates.Count >= ControlSnapshot.MaximumCandidates || clock.ElapsedMilliseconds >= 600 || characters >= 11800;
        // Visit each level before descending into long navigation/list subtrees.
        // A slow early branch must not hide the application's command bar from the model.
        var pending = new Queue<(AutomationElement Node, string? ParentId, int Depth, ShellMenuLocator.Binding? Menu)>();
        foreach (var menu in menuRoots) pending.Enqueue((menu.Element, null, 0, menu.Binding));
        if (root is not null) pending.Enqueue((root, null, 0, null));
        try
        {
        while (pending.TryDequeue(out var branch))
        {
            var (parent, parentId, depth, menu) = branch;
            if (Limited()) { partial = true; break; }
            if (depth >= 18) { partial = true; continue; }
            var cachedParent = ReferenceEquals(parent, root) || menuRoots.Any(r => ReferenceEquals(r.Element, parent)) ? parent : parent.GetUpdatedCache(cache);
            foreach (AutomationElement node in cachedParent.CachedChildren)
            {
                if (Limited()) { partial = true; break; }
                visited++;
                var properties = node.Cached;
                // XAML popups can report an offscreen structural parent while their edit/menu children are visible.
                // Candidate still rejects offscreen elements; traverse their bounded subtree to find visible children.
                if (properties.IsPassword || !identity.AllowsProcess(properties.ProcessId) ||
                    menu is not null && !ShellMenuLocator.AllowsNode(node, menu, identity)) continue;
                int[] runtime = node.GetRuntimeId();
                if (runtime.Length is 0 or > 64 || !seen.Add(string.Join(",", runtime))) continue;
                var candidate = Candidate(node, "c" + (candidates.Count + 1), parentId, frame, identity);
                if (rename is not null && candidate is not null)
                {
                    nint hwnd = (nint)properties.NativeWindowHandle;
                    if (hwnd == rename.Binding.Edit) candidate = null; // Same HWND, not a second document/input target.
                    else if (candidate.Focused && NativeShellRenameEdit.IsStructuralFocusAncestor(rename.Binding, hwnd, candidate.Role))
                        candidate = candidate with { Focused = false };
                }
                if (menu is not null && candidate?.Role != "MenuItem") candidate = null;
                string? childParent = parentId;
                if (candidate is not null)
                {
                    if (runtime.Length is > 0 and <= 64)
                    {
                        int candidateCharacters = candidate.Name.Length + (candidate.CurrentValue?.Length ?? 0);
                        if (characters + candidateCharacters > 12000) { partial = true; break; }
                        candidates.Add(candidate); references.Add(candidate.Id, runtime); characters += candidateCharacters;
                        if (menu is not null) menuBindings.Add(candidate.Id, menu);
                        childParent = candidate.Id;
                    }
                }
                pending.Enqueue((node, childParent, depth + 1, menu));
            }
        }
        }
        catch when ((desktopBackground is not null || menuBindings.Count > 0 || rename is not null) && identity.IsCurrent()) { partial = true; }
        if (menuBindings.Values.Any(menu => !ShellMenuLocator.IsCurrent(menu, identity))) throw new InvalidOperationException("SHELL_MENU_CHANGED");
        if (!identity.IsCurrent()) throw new InvalidOperationException("CONTROL_HOST_CHANGED");
        return Result(partial);
    }

    private static ControlCandidate? Candidate(AutomationElement element, string id, string? parent, Frame frame, HostedWindowIdentity identity)
    {
        var p = element.Cached;
        if (p.IsPassword || p.IsOffscreen || !identity.AllowsProcess(p.ProcessId)) return null;
        var r = p.BoundingRectangle;
        if (r.IsEmpty || !VisibleControlBounds.TryClip(r.Left, r.Top, r.Right, r.Bottom, frame.PhysicalRegion, out var bounds)) return null;
        string name = Clean(p.Name);
        if (name.Length == 0 && !p.IsKeyboardFocusable) return null;
        string? toggle = element.GetCachedPropertyValue(TogglePattern.ToggleStateProperty, true) is ToggleState state
            ? state switch { ToggleState.On => "on", ToggleState.Off => "off", _ => "indeterminate" } : null;
        bool? selected = element.GetCachedPropertyValue(SelectionItemPattern.IsSelectedProperty, true) is bool value ? value : null;
        string? expansion = element.GetCachedPropertyValue(ExpandCollapsePattern.ExpandCollapseStateProperty, true) is ExpandCollapseState expand
            ? expand switch { ExpandCollapseState.Collapsed => "collapsed", ExpandCollapseState.Expanded => "expanded",
                ExpandCollapseState.PartiallyExpanded => "partiallyExpanded", ExpandCollapseState.LeafNode => "leaf", _ => null } : null;
        var edit = p.ControlType == ControlType.Edit && p.HasKeyboardFocus && p.IsEnabled ? ReadEditMetadata(element) : null;
        return new(id, parent, name, p.ControlType.ProgrammaticName.Replace("ControlType.", "", StringComparison.Ordinal), bounds,
            p.IsEnabled, p.HasKeyboardFocus, p.IsKeyboardFocusable, toggle, selected)
        {
            CurrentValue = edit?.Value, ValueTruncated = edit?.Truncated, SelectionStart = edit?.Start, SelectionLength = edit?.Length,
            ExpandCollapseState = expansion, HasSubmenu = p.ControlType == ControlType.MenuItem && expansion is not null ? expansion != "leaf" : null,
            LocalIdentity = OpaqueControlIdentity(identity, element.GetRuntimeId())
        };
    }

    private static string OpaqueControlIdentity(HostedWindowIdentity identity, int[] runtimeId) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(":",
            identity.HostProcessId, identity.HostStartTimeUtcTicks, identity.HostHwnd.ToInt64(),
            identity.ContentProcessId, identity.ContentStartTimeUtcTicks, string.Join(",", runtimeId)))));

    private sealed record EditMetadata(string? Value, bool? Truncated, int? Start = null, int? Length = null);
    private static EditMetadata? ReadEditMetadata(AutomationElement element)
    {
        // Never request ValueProperty in the general cache: only the visible focused non-password Edit may be read.
        // Document controls (including document editors) are not included in this narrow field observation.
        const int limit = ControlCandidate.MaximumValueLength;
        try
        {
            var before = element.Current;
            if (before.ControlType != ControlType.Edit || before.IsPassword || before.IsOffscreen || !before.IsEnabled || !before.HasKeyboardFocus) return null;
            nint nativeEdit = (nint)before.NativeWindowHandle;
            var nativeClass = new StringBuilder(256);
            if (nativeEdit != 0 && GetClassNameW(nativeEdit, nativeClass, nativeClass.Capacity) > 0 && nativeClass.ToString() == "Edit" &&
                (GetWindowLongPtrW(nativeEdit, -16).ToInt64() & 0x20) != 0) return null;
            TextPattern? pattern = null;
            string? text = null;
            if (element.TryGetCurrentPattern(TextPattern.Pattern, out var raw) && raw is TextPattern textPattern)
            {
                pattern = textPattern;
                text = pattern.DocumentRange.GetText(limit + 1);
            }
            else if (element.TryGetCurrentPattern(ValuePattern.Pattern, out raw) && raw is ValuePattern valuePattern)
                text = valuePattern.Current.Value;
            if (element.Current.IsPassword || !element.Current.HasKeyboardFocus || text is null) return null;
            if (text.Length > limit) return new(null, true);
            if (text.Contains('\0') || !WellFormedUtf16(text)) return null;
            var result = new EditMetadata(text, false);
            if (pattern is not null)
            {
                try
                {
                    var ranges = pattern.GetSelection();
                    var document = pattern.DocumentRange;
                    if (ranges.Length == 1 && document.GetText(limit + 1) == text &&
                        document.CompareEndpoints(TextPatternRangeEndpoint.Start, ranges[0], TextPatternRangeEndpoint.Start) <= 0 &&
                        document.CompareEndpoints(TextPatternRangeEndpoint.End, ranges[0], TextPatternRangeEndpoint.End) >= 0)
                    {
                        var prefix = document.Clone();
                        prefix.MoveEndpointByRange(TextPatternRangeEndpoint.End, ranges[0], TextPatternRangeEndpoint.Start);
                        string leading = prefix.GetText(limit + 1), selection = ranges[0].GetText(limit + 1);
                        if (leading.Length <= text.Length && selection.Length <= text.Length - leading.Length &&
                            text.StartsWith(leading, StringComparison.Ordinal) && text.AsSpan(leading.Length, selection.Length).SequenceEqual(selection))
                            result = result with { Start = leading.Length, Length = selection.Length };
                    }
                }
                catch { /* Unavailable selection stays unknown; it never means an empty or complete selection. */ }
            }
            if (result.Start is null)
            {
                // Standard Explorer filename Edit exposes a read-only EM_GETSEL even when TextPattern is absent.
                nint hwnd = (nint)before.NativeWindowHandle;
                var className = new StringBuilder(256);
                if (hwnd != 0 && GetClassNameW(hwnd, className, className.Capacity) > 0 && className.ToString() == "Edit" &&
                    GetWindowThreadProcessId(hwnd, out uint pid) != 0 && pid == before.ProcessId &&
                    (GetWindowLongPtrW(hwnd, -16).ToInt64() & 0x20) == 0 &&
                    SendMessageTimeoutW(hwnd, 0x00B0, 0, 0, 0x0003, 80, out nuint selection) != 0)
                {
                    int start = (int)(selection & 0xFFFF), end = (int)((selection >> 16) & 0xFFFF);
                    if (start <= end && end <= text.Length) result = result with { Start = start, Length = end - start };
                }
            }
            if (element.Current.IsPassword || !element.Current.HasKeyboardFocus) return null;
            string? refreshed = pattern?.DocumentRange.GetText(limit + 1);
            if (pattern is null && element.TryGetCurrentPattern(ValuePattern.Pattern, out var latest) && latest is ValuePattern latestValue)
                refreshed = latestValue.Current.Value;
            if (refreshed != text) return null;
            return result;
        }
        catch { return null; }
    }

    private static bool WellFormedUtf16(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsHighSurrogate(text[i])) { if (++i >= text.Length || !char.IsLowSurrogate(text[i])) return false; }
            else if (char.IsLowSurrogate(text[i])) return false;
        }
        return true;
    }

    private static ControlResolution Resolve(Frame frame, ControlCandidate expected, int[] runtimeId, HostedWindowIdentity identity, Action<object>? trace)
    {
        using var dpi = new PhysicalDpiScope();
        if (!identity.IsCurrent()) return new(null, "CONTROL_HOST_CHANGED");
        var point = new PhysicalPoint(expected.Bounds.Left + (expected.Bounds.Width - 1) / 2, expected.Bounds.Top + (expected.Bounds.Height - 1) / 2);
        var cache = Cache();
        var hit = AutomationElement.FromPoint(new Point(point.X, point.Y))?.GetUpdatedCache(cache);
        if (trace is not null)
        {
            nint nativeHit = WindowFromPoint(new(point.X, point.Y));
            _ = GetWindowThreadProcessId(nativeHit, out uint nativePid);
            var nativeClass = new StringBuilder(128); _ = GetClassNameW(nativeHit, nativeClass, nativeClass.Capacity);
            trace(new { stage = "control_hit", expected.Id, expected.Role, expected.Bounds, runtimeId, point,
                nativeHwnd = nativeHit.ToString("X"), nativePid, nativeClass = nativeClass.ToString(),
                uiaProcessId = hit?.Cached.ProcessId, uiaRole = hit?.Cached.ControlType.ProgrammaticName,
                uiaHwnd = hit?.Cached.NativeWindowHandle, uiaRuntimeId = hit?.GetRuntimeId() });
        }
        var node = hit;
        for (int depth = 0; node is not null && depth < 8; depth++, node = TreeWalker.ControlViewWalker.GetParent(node, cache))
        {
            if (!identity.AllowsProcess(node.Cached.ProcessId)) break;
            if (!node.GetRuntimeId().SequenceEqual(runtimeId)) continue;
            if (!HasHostAncestor(node, identity, cache)) return new(null, "CONTROL_HOST_ANCESTOR_MISMATCH");
            var actual = Candidate(node, expected.Id, expected.ParentId, frame, identity);
            if (actual is null || !actual.Enabled || actual.Bounds != expected.Bounds || actual.Name != expected.Name || actual.Role != expected.Role ||
                actual.ToggleState != expected.ToggleState || actual.Selected != expected.Selected || actual.CurrentValue != expected.CurrentValue ||
                actual.ValueTruncated != expected.ValueTruncated || actual.SelectionStart != expected.SelectionStart || actual.SelectionLength != expected.SelectionLength ||
                actual.ExpandCollapseState != expected.ExpandCollapseState || actual.HasSubmenu != expected.HasSubmenu) return new(null, "CONTROL_CHANGED");
            if (!identity.IsCurrent()) return new(null, "CONTROL_HOST_CHANGED");
            return new(point, null);
        }
        var shellItem = ResolveShellListItem(frame, expected, runtimeId, identity, point, cache, trace);
        return shellItem ?? ResolveExplorerBridge(frame, expected, runtimeId, identity, hit, point, cache, trace);
    }

    // A transparent overlay may replace the global UIA hit while native mouse hit testing reaches
    // Explorer's actual SysListView32. Resolve only the requested ListItem inside that exact native list.
    // A real native hit on another app/overlay is still rejected; there is no name or image fallback.
    private static ControlResolution? ResolveShellListItem(Frame frame, ControlCandidate expected, int[] runtimeId,
        HostedWindowIdentity identity, PhysicalPoint point, CacheRequest cache, Action<object>? trace)
    {
        if (expected.Role != "ListItem" || identity.ContentHwnd is not null ||
            !string.Equals(identity.HostProcessName, "explorer", StringComparison.OrdinalIgnoreCase) ||
            GetForegroundWindow() != identity.HostHwnd || frame.OwnWindowRects.Any(r => FrameChecks.Contains(r, point))) return null;
        bool knownHost = NativeWindow(identity.HostHwnd, identity, "Progman", null) || NativeWindow(identity.HostHwnd, identity, "WorkerW", null) ||
            NativeWindow(identity.HostHwnd, identity, "CabinetWClass", null);
        nint list = WindowFromPoint(new(point.X, point.Y)), view = GetParent(list);
        if (!knownHost || !NativeWindow(list, identity, "SysListView32", view) || !IsWindowEnabled(list) ||
            !NativeWindow(view, identity, "SHELLDLL_DefView", null)) return null;
        var childCache = Cache(); childCache.TreeScope = TreeScope.Element | TreeScope.Children;
        var root = AutomationElement.FromHandle(list).GetUpdatedCache(childCache);
        if (root.Cached.ProcessId != identity.HostProcessId || root.Cached.NativeWindowHandle != unchecked((int)list) ||
            root.Cached.ControlType != ControlType.List || root.Cached.IsOffscreen || root.Cached.IsPassword || !root.Cached.IsEnabled ||
            !root.Cached.BoundingRectangle.Contains(new Point(point.X, point.Y))) return new(null, "SHELL_LIST_ROOT_UNVERIFIED");
        int[] listRuntime = root.GetRuntimeId();
        var watch = Stopwatch.StartNew();
        var pending = new Queue<(AutomationElement Element, int Depth)>(); pending.Enqueue((root, 0));
        var seen = new HashSet<string>(StringComparer.Ordinal) { string.Join(",", listRuntime) };
        int visited = 0, matches = 0, coveringItems = 0; bool limited = false;
        AutomationElement? found = null;
        while (pending.TryDequeue(out var branch))
        {
            if (branch.Depth > 8 || watch.ElapsedMilliseconds >= 350) { limited = true; break; }
            var parent = ReferenceEquals(branch.Element, root) ? root : branch.Element.GetUpdatedCache(childCache);
            foreach (AutomationElement child in parent.CachedChildren)
            {
                if (++visited > 384 || watch.ElapsedMilliseconds >= 350) { limited = true; break; }
                var p = child.Cached;
                if (p.ProcessId != identity.HostProcessId || p.IsPassword) continue;
                int[] runtime = child.GetRuntimeId();
                if (runtime.Length is 0 or > 64 || !seen.Add(string.Join(",", runtime))) continue;
                if (runtime.SequenceEqual(runtimeId)) { matches++; found = child; }
                if (p.ControlType == ControlType.ListItem && !p.IsOffscreen && p.BoundingRectangle.Contains(new Point(point.X, point.Y))) coveringItems++;
                pending.Enqueue((child, branch.Depth + 1));
            }
            if (limited) break;
        }
        trace?.Invoke(new { stage = "shell_list_subtree", expected.Id, listHwnd = list.ToString("X"), listRuntime, visited, matches, coveringItems,
            limited, elapsedMs = watch.ElapsedMilliseconds });
        if (limited || matches != 1 || coveringItems != 1 || found is null) return new(null, "SHELL_LIST_ITEM_NOT_UNIQUE");
        var fresh = found.GetUpdatedCache(cache);
        if (!fresh.GetRuntimeId().SequenceEqual(runtimeId) || !HasHostAncestor(fresh, identity, cache) ||
            Candidate(fresh, expected.Id, expected.ParentId, frame, identity) != expected ||
            !fresh.Cached.BoundingRectangle.Contains(new Point(point.X, point.Y))) return new(null, "SHELL_LIST_ITEM_CHANGED");
        if (GetForegroundWindow() != identity.HostHwnd || WindowFromPoint(new(point.X, point.Y)) != list ||
            !NativeWindow(list, identity, "SysListView32", view) || !IsWindowEnabled(list) || !NativeWindow(view, identity, "SHELLDLL_DefView", null) ||
            !AutomationElement.FromHandle(list).GetRuntimeId().SequenceEqual(listRuntime) || !identity.IsCurrent())
            return new(null, "SHELL_LIST_HIT_CHANGED");
        return new(point, null);
    }

    // Explorer's XAML provider can hit its native input bridge instead of a button in that bridge's own subtree.
    // This exception never searches the host or accepts a same-process sibling/overlay as the requested control.
    private static ControlResolution ResolveExplorerBridge(Frame frame, ControlCandidate expected, int[] runtimeId,
        HostedWindowIdentity identity, AutomationElement? hit, PhysicalPoint point, CacheRequest cache, Action<object>? trace)
    {
        const string childBridgeClass = "Microsoft.UI.Content.DesktopChildSiteBridge";
        const string popupBridgeClass = "Microsoft.UI.Content.PopupWindowSiteBridge";
        if (hit is null || !string.Equals(identity.HostProcessName, "explorer", StringComparison.OrdinalIgnoreCase) ||
            identity.ContentHwnd is not null || hit.Cached.ControlType != ControlType.Pane ||
            !NativeWindow(identity.HostHwnd, identity, "CabinetWClass", null)) return new(null, "CONTROL_OCCLUDED_OR_GONE");
        nint bridge = (nint)hit.Cached.NativeWindowHandle;
        // WinUI menu bridges are owned top-level windows, unlike the toolbar's child bridge.
        bool popup = NativeWindow(bridge, identity, popupBridgeClass, identity.HostHwnd, expectedRoot: bridge) &&
            GetWindow(bridge, 4) == identity.HostHwnd;
        string bridgeClass = popup ? popupBridgeClass : childBridgeClass;
        nint bridgeRoot = popup ? bridge : identity.HostHwnd;
        if (!NativeWindow(bridge, identity, bridgeClass, identity.HostHwnd, expectedRoot: bridgeRoot) || hit.Cached.ProcessId != identity.HostProcessId ||
            !HasHostAncestor(hit, identity, cache) || !BridgeContains(hit, bridge, expected.Bounds)) return new(null, "CONTROL_OCCLUDED_OR_GONE");
        int[] bridgeRuntime = hit.GetRuntimeId();
        var clock = Stopwatch.StartNew();
        int visited = 0, matches = 0;
        bool limited = false;
        AutomationElement? found = null;
        var childCache = Cache();
        childCache.TreeScope = TreeScope.Element | TreeScope.Children;
        var seen = new HashSet<string>(StringComparer.Ordinal) { string.Join(",", bridgeRuntime) };
        void Walk(AutomationElement parent, int depth)
        {
            if (depth >= 18 || visited >= 384 || clock.ElapsedMilliseconds >= 400) { limited = true; return; }
            foreach (AutomationElement child in parent.GetUpdatedCache(childCache).CachedChildren)
            {
                if (++visited > 384 || clock.ElapsedMilliseconds >= 400) { limited = true; return; }
                if (child.Cached.ProcessId != identity.HostProcessId || child.Cached.IsPassword) continue;
                int[] childRuntime = child.GetRuntimeId();
                if (childRuntime.Length is 0 or > 64 || !seen.Add(string.Join(",", childRuntime))) continue;
                if (childRuntime.SequenceEqual(runtimeId)) { matches++; found = child; }
                if (matches > 1) return;
                Walk(child, depth + 1);
                if (limited || matches > 1) return;
            }
        }
        Walk(hit, 0);
        trace?.Invoke(new { stage = "bridge_subtree", expected.Id, expected.Name, visited, matches, limited, elapsedMs = clock.ElapsedMilliseconds });
        if (limited || matches != 1 || found is null) return new(null, "CONTROL_EXPLORER_BRIDGE_TARGET_UNAVAILABLE");
        var fresh = found.GetUpdatedCache(cache);
        if (!fresh.GetRuntimeId().SequenceEqual(runtimeId) || !HasHostAncestor(fresh, identity, cache) ||
            Candidate(fresh, expected.Id, expected.ParentId, frame, identity) != expected)
            return new(null, "CONTROL_CHANGED");
        nint inputSite = ExplorerInputSite(fresh, bridge, bridgeRoot, bridgeRuntime, identity, cache, trace,
            allowSharedPopupAnchor: popup && expected.Role == "MenuItem", out nint anchorBridge, out nint anchorRoot);
        if (inputSite == 0) return new(null, "CONTROL_EXPLORER_BRIDGE_PATH_MISMATCH");
        var finalHit = AutomationElement.FromPoint(new Point(point.X, point.Y))?.GetUpdatedCache(cache);
        nint nativeHit = WindowFromPoint(new NativePoint(point.X, point.Y));
        if (finalHit is null || finalHit.Cached.ControlType != ControlType.Pane || finalHit.Cached.ProcessId != identity.HostProcessId ||
            finalHit.Cached.NativeWindowHandle != (int)bridge || !finalHit.GetRuntimeId().SequenceEqual(bridgeRuntime) ||
            nativeHit != bridge && nativeHit != inputSite || !BridgeContains(finalHit, bridge, expected.Bounds) ||
            !NativeWindow(bridge, identity, bridgeClass, identity.HostHwnd, expectedRoot: bridgeRoot) ||
            popup && GetWindow(bridge, 4) != identity.HostHwnd ||
            !NativeWindow(inputSite, identity, "InputSiteWindowClass", anchorBridge, requireVisible: false, expectedRoot: anchorRoot) ||
            anchorBridge != bridge && !NativeWindow(anchorBridge, identity, childBridgeClass, identity.HostHwnd) ||
            !HasHostAncestor(finalHit, identity, cache) || !identity.IsCurrent())
            return new(null, "CONTROL_EXPLORER_BRIDGE_CHANGED");
        return new(point, null);
    }

    private static nint ExplorerInputSite(AutomationElement target, nint bridge, nint bridgeRoot, int[] bridgeRuntime,
        HostedWindowIdentity identity, CacheRequest cache, Action<object>? trace, bool allowSharedPopupAnchor,
        out nint anchorBridge, out nint anchorRoot)
    {
        nint inputSite = 0;
        anchorBridge = bridge;
        anchorRoot = bridgeRoot;
        for (int depth = 0; target is not null && depth < 24; depth++, target = TreeWalker.ControlViewWalker.GetParent(target, cache))
        {
            if (target.Cached.ProcessId != identity.HostProcessId) return 0;
            nint hwnd = (nint)target.Cached.NativeWindowHandle;
            if (trace is not null)
            {
                var nativeClass = new StringBuilder(256);
                if (hwnd != 0) GetClassNameW(hwnd, nativeClass, nativeClass.Capacity);
                trace(new { atUtc = DateTimeOffset.UtcNow, depth, name = Clean(target.Cached.Name),
                    role = target.Cached.ControlType?.ProgrammaticName, hwnd = hwnd.ToString("X"),
                    nativeClass = nativeClass.ToString(), parent = GetParent(hwnd).ToString("X"),
                    root = GetAncestor(hwnd, 2).ToString("X"), visible = IsWindowVisible(hwnd),
                    bridge = bridge.ToString("X"), bridgeRoot = bridgeRoot.ToString("X"),
                    runtime = target.GetRuntimeId(), bridgeRuntime });
            }
            if (hwnd == 0) continue;
            if (inputSite == 0)
            {
                // The provider's InputSiteWindowClass is a hidden native anchor; the rendering/hit bridge and actual target must be visible.
                if (target.Cached.ControlType != ControlType.Pane) return 0;
                if (!NativeWindow(hwnd, identity, "InputSiteWindowClass", bridge, requireVisible: false, expectedRoot: bridgeRoot))
                {
                    nint sharedBridge = GetParent(hwnd);
                    if (!allowSharedPopupAnchor || !NativeWindow(sharedBridge, identity, "Microsoft.UI.Content.DesktopChildSiteBridge", identity.HostHwnd) ||
                        !NativeWindow(hwnd, identity, "InputSiteWindowClass", sharedBridge, requireVisible: false)) return 0;
                    anchorBridge = sharedBridge;
                    anchorRoot = identity.HostHwnd;
                }
                inputSite = hwnd;
            }
            else
            {
                if (hwnd != anchorBridge || target.Cached.ControlType != ControlType.Pane || !HasHostAncestor(target, identity, cache)) return 0;
                var nativeAnchor = AutomationElement.FromHandle(anchorBridge);
                if (!target.GetRuntimeId().SequenceEqual(nativeAnchor.GetRuntimeId())) return 0;
                return anchorBridge != bridge || target.GetRuntimeId().SequenceEqual(bridgeRuntime) ? inputSite : 0;
            }
        }
        return 0;
    }

    private static bool NativeWindow(nint hwnd, HostedWindowIdentity identity, string className, nint? parent, bool requireVisible = true, nint? expectedRoot = null)
    {
        if (hwnd == 0 || !IsWindow(hwnd) || requireVisible && !IsWindowVisible(hwnd) || GetAncestor(hwnd, 2) != (expectedRoot ?? identity.HostHwnd) ||
            GetWindowThreadProcessId(hwnd, out uint pid) == 0 || pid != identity.HostProcessId || parent.HasValue && GetParent(hwnd) != parent.Value) return false;
        var actual = new StringBuilder(256);
        return GetClassNameW(hwnd, actual, actual.Capacity) > 0 && actual.ToString() == className;
    }

    private static bool BridgeContains(AutomationElement bridge, nint hwnd, PhysicalRect bounds) =>
        !bridge.Cached.IsOffscreen && !bridge.Cached.IsPassword && bridge.Cached.IsEnabled &&
        bridge.Cached.BoundingRectangle.Contains(new Rect(bounds.Left, bounds.Top, bounds.Width, bounds.Height)) &&
        GetWindowRect(hwnd, out NativeRect rect) && rect.Left <= bounds.Left && rect.Top <= bounds.Top && rect.Right >= bounds.Right && rect.Bottom >= bounds.Bottom;

    [StructLayout(LayoutKind.Sequential)] private readonly record struct NativePoint(int X, int Y);
    [StructLayout(LayoutKind.Sequential)] private struct NativeRect { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern nint WindowFromPoint(NativePoint point);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindow(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowVisible(nint hwnd);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool IsWindowEnabled(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetWindowRect(nint hwnd, out NativeRect rect);
    [DllImport("user32.dll")] private static extern nint GetAncestor(nint hwnd, uint flags);
    [DllImport("user32.dll")] private static extern nint GetParent(nint hwnd);
    [DllImport("user32.dll")] private static extern nint GetWindow(nint hwnd, uint command);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassNameW(nint hwnd, StringBuilder name, int maximum);
    [DllImport("user32.dll")] private static extern nint GetWindowLongPtrW(nint hwnd, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint SendMessageTimeoutW(nint hwnd, uint message, nuint wParam, nint lParam, uint flags, uint timeout, out nuint result);

    private static bool IsHostRoot(AutomationElement node, HostedWindowIdentity identity) =>
        node.Cached.ProcessId == identity.HostProcessId && node.Cached.NativeWindowHandle == unchecked((int)identity.HostHwnd);

    private static bool HasHostAncestor(AutomationElement node, HostedWindowIdentity identity, CacheRequest cache)
    {
        for (int depth = 0; node is not null && depth < 24; depth++, node = TreeWalker.ControlViewWalker.GetParent(node, cache))
        {
            if (!identity.AllowsProcess(node.Cached.ProcessId)) return false;
            if (IsHostRoot(node, identity)) return true;
        }
        return false;
    }

    private static string Clean(string? name)
    {
        if (name is null) return "";
        var result = new StringBuilder();
        foreach (var rune in name.EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            if (result.Length + rune.Utf16SequenceLength > 128) break;
            result.Append(rune);
        }
        return result.ToString().Trim();
    }
    public void Dispose() { _worker.Dispose(); _current = null; }
}

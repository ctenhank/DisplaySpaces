using BetterVirtualDesktop.App.Native;
using BetterVirtualDesktop.Core;

namespace BetterVirtualDesktop.App.Services;

public sealed class DesktopController
{
    private readonly Dictionary<long, NativeWindowInfo> _windows = new();
    private readonly Dictionary<long, DateTime> _showRequests = new();
    private readonly Func<NativeWindowInfo, bool> _include;
    private readonly Func<bool> _canHide;
    private readonly RecoveryJournal _journal;
    private readonly HashSet<long> _unmanageable = new();
    private readonly Dictionary<long, PendingDisplayMove> _displayMoves = new();
    public WorkspaceCatalog Catalog { get; } = new();
    public bool Paused { get; private set; }
    public event Action? Changed;
    public IReadOnlyDictionary<long, NativeWindowInfo> Windows => _windows;
    public int UnmanageableCount => _unmanageable.Count;

    public DesktopController(RecoveryJournal journal, Func<NativeWindowInfo, bool>? include = null,
        Func<bool>? canHide = null)
    {
        _journal = journal;
        _include = include ?? (_ => true);
        _canHide = canHide ?? (() => true);
        Refresh();
    }

    public string PointerDisplay => Screen.FromPoint(Cursor.Position).DeviceName;

    public void Refresh()
    {
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return;
        Catalog.SynchronizeDisplays(screens.Select((s, i) => new DisplayDescriptor(s.DeviceName,
            Localization.UiText.Get("DisplayDefault", i + 1) + (s.Primary ? Localization.UiText.Get("PrimaryDisplaySuffix") : ""), s.Primary)));
        _journal.PruneClosed();
        var current = NativeWindows.EnumerateWindows().Where(_include).ToDictionary(w => w.Handle);
        foreach (var handle in _windows.Keys.ToArray())
        {
            if (!current.TryGetValue(handle, out var window) || !SameIdentity(_windows[handle], window))
            { Forget(handle); continue; }
            _windows[handle] = window;
            if (!window.IsVisible && !_journal.Contains(handle)) { Forget(handle); continue; }
            if (!NativeWindows.IsOnCurrentVirtualDesktop(handle))
            {
                if (_journal.Contains(handle)) RequestShow(handle);
                if (!_journal.Contains(handle)) Forget(handle);
                continue;
            }
            if (window.IsVisible && !_journal.Contains(handle) && !window.IsMinimized && !_displayMoves.ContainsKey(handle))
                Catalog.AssignToDisplay(handle, ResolveDisplay(window.Handle));
        }

        // Representatives first, so newly opened modal dialogs can inherit their owner's desktop.
        foreach (var window in current.Values.OrderBy(w => w.RootOwner != w.Handle))
        {
            if (_windows.ContainsKey(window.Handle) || !window.IsVisible || window.IsCloaked
                || !NativeWindows.IsOnCurrentVirtualDesktop(window.Handle)) continue;
            if (window.ProcessStartTicks == 0) { _unmanageable.Add(window.Handle); continue; }
            _windows.Add(window.Handle, window);
            long owner = window.RootOwner;
            var ownerAssignment = owner == 0 ? null : Catalog.GetAssignment(owner);
            Catalog.TrackWindow(window.Handle, ownerAssignment?.DisplayId ?? ResolveDisplay(window.Handle));
            if (ownerAssignment is not null) Catalog.MoveWindowToDesktop(window.Handle, ownerAssignment.DesktopId);
        }
        // Keep owned popup windows with their representative when the owner is dragged to another display.
        foreach (var window in _windows.Values)
        {
            if (window.RootOwner == 0 || window.RootOwner == window.Handle) continue;
            var owner = Catalog.GetAssignment(window.RootOwner);
            if (owner is null) continue;
            Catalog.AssignToDisplay(window.Handle, owner.DisplayId);
            Catalog.MoveWindowToDesktop(window.Handle, owner.DesktopId);
        }
        ReconcileDisplayMoves(screens);
        _unmanageable.RemoveWhere(h => !current.ContainsKey(h));
        ApplyVisibility();
        Changed?.Invoke();
    }

    private static bool SameIdentity(NativeWindowInfo a, NativeWindowInfo b) =>
        a.ProcessId == b.ProcessId && a.ProcessStartTicks == b.ProcessStartTicks && a.ClassName == b.ClassName;

    private string ResolveDisplay(long handle)
    {
        string id = NativeWindows.GetDisplayId(handle);
        var displays = Catalog.Snapshot();
        return displays.Any(d => d.Id == id) ? id : displays.First(d => d.IsPrimary || displays.Count == 1).Id;
    }

    private void Forget(long handle)
    {
        _windows.Remove(handle);
        _unmanageable.Remove(handle);
        _showRequests.Remove(handle);
        _displayMoves.Remove(handle);
        Catalog.ForgetWindow(handle);
    }

    private void RequestShow(long handle)
    {
        if (_showRequests.TryGetValue(handle, out var requested) && DateTime.UtcNow - requested > TimeSpan.FromMilliseconds(80))
        {
            _journal.ConfirmRestored(handle);
            if (!_journal.Contains(handle)) { _showRequests.Remove(handle); return; }
        }
        _journal.Restore(handle);
        _showRequests[handle] = DateTime.UtcNow;
    }

    private void ApplyVisibility()
    {
        if (!_canHide()) Paused = true;
        // A tracked window can cease to satisfy enumeration filters (for example, its
        // title or styles change). Losing its catalog entry must not strand it hidden.
        foreach (var entry in _journal.Entries.ToArray())
            if (!_windows.ContainsKey(entry.Handle)) RequestShow(entry.Handle);

        var toHide = new List<NativeWindowInfo>();
        var blockedGroups = new HashSet<long>();
        // Prepare every member before hiding any owner: Windows can hide owned popups implicitly.
        foreach (var window in _windows.Values)
        {
            bool visible = Paused || Catalog.IsWindowVisible(window.Handle)
                || !NativeWindows.IsOnCurrentVirtualDesktop(window.Handle);
            if (visible)
            {
                if (_journal.Contains(window.Handle)) RequestShow(window.Handle);
                continue;
            }
            _showRequests.Remove(window.Handle);
            if (!NativeWindows.IsVisible(window.Handle) && !_journal.Contains(window.Handle)) continue;
            if (_journal.PrepareHide(window))
            {
                toHide.Add(window);
                _unmanageable.Remove(window.Handle);
            }
            else
            {
                _unmanageable.Add(window.Handle);
                blockedGroups.Add(OwnerGroup(window));
            }
        }
        foreach (var window in toHide.OrderBy(w => w.RootOwner == w.Handle))
        {
            // A palette/dialog that cannot be journaled makes its complete owner
            // group unsafe to hide: Windows may hide owned windows implicitly.
            if (blockedGroups.Contains(OwnerGroup(window))) RequestShow(window.Handle);
            else NativeWindows.Hide(window.Handle);
        }
    }

    private long OwnerGroup(NativeWindowInfo window) =>
        _windows.ContainsKey(window.RootOwner) ? window.RootOwner : window.Handle;

    public void Switch(string displayId, Guid desktopId)
    {
        Catalog.SwitchToDesktop(displayId, desktopId);
        ApplyVisibility();
        Changed?.Invoke();
    }

    public void SwitchRelative(string displayId, int delta)
    {
        Catalog.SwitchRelative(displayId, delta);
        ApplyVisibility();
        Changed?.Invoke();
    }

    public void AddDesktop(string displayId)
    {
        var desktop = Catalog.AddDesktop(displayId);
        Switch(displayId, desktop);
    }

    public void RemoveDesktop(string displayId, Guid desktopId)
    {
        if (Catalog.Snapshot().First(d => d.Id == displayId).Desktops.Count <= 1) return;
        Catalog.RemoveDesktop(displayId, desktopId);
        ApplyVisibility();
        Changed?.Invoke();
    }

    public void RenameDesktop(string displayId, Guid desktopId, string name)
    {
        Catalog.RenameDesktop(displayId, desktopId, name);
        Changed?.Invoke();
    }

    public void MoveWindow(long handle, Guid desktopId)
    {
        if (!_windows.TryGetValue(handle, out var window)) return;
        var assignment = Catalog.GetAssignment(OwnerGroup(window));
        if (assignment is not null) MoveWindowToDisplay(handle, assignment.DisplayId, desktopId);
    }

    /// <summary>Creates an inactive desktop and moves the selected owner group there without switching displays.</summary>
    public Guid? MoveWindowToNewDesktop(long handle, string displayId)
    {
        if (!_windows.TryGetValue(handle, out var selected)
            || !Catalog.Snapshot().Any(d => d.Id == displayId)
            || !Screen.AllScreens.Any(s => s.DeviceName == displayId)) return null;
        long root = OwnerGroup(selected);
        var original = Catalog.GetAssignment(root);
        if (original is null || !Screen.AllScreens.Any(s => s.DeviceName == original.DisplayId)) return null;
        var members = _windows.Values.Where(w => w.Handle == root || w.RootOwner == root).ToArray();
        if (members.Any(w => _displayMoves.ContainsKey(w.Handle)
            || !NativeWindows.IsSameWindow(w.Handle, w.ProcessId, w.ProcessStartTicks, w.ClassName)
            || !NativeWindows.IsOnCurrentVirtualDesktop(w.Handle))) return null;

        Guid created = Catalog.AddDesktop(displayId);
        bool moved = false;
        try
        {
            moved = MoveWindowToDisplay(handle, displayId, created);
            return moved ? created : null;
        }
        finally
        {
            if (!moved)
            {
                // An exception after assignment can still leave a valid move in the catalog.
                // Remove only our untouched empty desktop; never migrate windows as rollback.
                var display = Catalog.Snapshot().FirstOrDefault(d => d.Id == displayId);
                var desktop = display?.Desktops.FirstOrDefault(d => d.Id == created);
                if (display is { Desktops.Count: > 1 } && display.ActiveDesktopId != created
                    && desktop is { WindowIds.Count: 0 }) Catalog.RemoveDesktop(displayId, created);
            }
        }
    }

    /// <summary>Moves a complete owner/dialog group to the chosen display and desktop, without switching either display.</summary>
    public bool MoveWindowToDisplay(long handle, string displayId, Guid desktopId)
    {
        if (!_windows.TryGetValue(handle, out var selected)) return false;
        var destination = Catalog.Snapshot().FirstOrDefault(d => d.Id == displayId);
        if (destination is null || !destination.Desktops.Any(d => d.Id == desktopId)) return false;
        long root = OwnerGroup(selected);
        var original = Catalog.GetAssignment(root);
        if (original is null) return false;
        var members = _windows.Values.Where(w => w.Handle == root || w.RootOwner == root)
            .OrderBy(w => w.Handle != root).ToArray();
        if (members.Any(w => !NativeWindows.IsSameWindow(w.Handle, w.ProcessId, w.ProcessStartTicks, w.ClassName)
            || !NativeWindows.IsOnCurrentVirtualDesktop(w.Handle))) return false;
        bool crossDisplay = original.DisplayId != displayId;
        var screens = Screen.AllScreens;
        var target = screens.FirstOrDefault(s => s.DeviceName == displayId);
        var source = screens.FirstOrDefault(s => s.DeviceName == original.DisplayId);
        if (target is null || source is null || (crossDisplay && members.Any(w => _displayMoves.ContainsKey(w.Handle)))) return false;
        var newlyPrepared = new List<long>();
        if (!Paused && _canHide() && destination.ActiveDesktopId != desktopId)
        {
            // Establish recoverability for the entire group before moving it to an inactive
            // desktop. An inaccessible owned dialog must not be left visible on that desktop.
            foreach (var member in members)
            {
                bool existed = _journal.Contains(member.Handle);
                if (!_journal.PrepareHide(member))
                {
                    _unmanageable.Add(member.Handle);
                    foreach (long prepared in newlyPrepared) _journal.ConfirmRestored(prepared);
                    return false;
                }
                if (!existed) newlyPrepared.Add(member.Handle);
            }
        }
        if (crossDisplay && !NativeWindows.MoveGroupToDisplay(members, source, target))
        {
            foreach (long prepared in newlyPrepared) _journal.ConfirmRestored(prepared);
            return false;
        }
        Catalog.MoveWindowsToDisplay(members.Select(w => w.Handle), displayId, desktopId);
        // Native placement is posted to the owning UI thread. Discovery must not interpret its
        // old coordinates as a manual drag back while that message is still in flight.
        if (crossDisplay)
        {
            var pending = new PendingDisplayMove(members.Select(w => w.Handle).ToArray(), original,
                displayId, DateTime.UtcNow.AddSeconds(3));
            foreach (var member in members) _displayMoves[member.Handle] = pending;
        }
        ApplyVisibility();
        Changed?.Invoke();
        return true;
    }

    private void ReconcileDisplayMoves(Screen[] screens)
    {
        foreach (var move in _displayMoves.Values.Distinct().ToArray())
        {
            var members = move.Handles.Where(_windows.ContainsKey).Select(h => _windows[h]).ToArray();
            bool arrived = members.All(w => NativeWindows.GetDisplayId(w.Handle) == move.TargetDisplayId);
            bool disconnected = !screens.Any(s => s.DeviceName == move.TargetDisplayId);
            if (!arrived && !disconnected && DateTime.UtcNow < move.ExpiresAt) continue;
            foreach (long member in move.Handles) _displayMoves.Remove(member);
            if (arrived || disconnected || members.Length == 0) continue;
            // A foreign application may reject or override an asynchronous placement. Restore
            // the previous assignment instead of retaining an invisible logical/physical mismatch.
            var originalDisplay = Catalog.Snapshot().FirstOrDefault(d => d.Id == move.Original.DisplayId);
            var originalScreen = screens.FirstOrDefault(s => s.DeviceName == move.Original.DisplayId);
            var targetScreen = screens.FirstOrDefault(s => s.DeviceName == move.TargetDisplayId);
            if (originalDisplay is null || originalScreen is null || targetScreen is null) continue;
            Guid desktop = originalDisplay.Desktops.Any(d => d.Id == move.Original.DesktopId)
                ? move.Original.DesktopId : originalDisplay.ActiveDesktopId;
            NativeWindows.MoveGroupToDisplay(members, targetScreen, originalScreen);
            Catalog.MoveWindowsToDisplay(members.Select(w => w.Handle), originalDisplay.Id, desktop);
        }
    }

    private sealed record PendingDisplayMove(long[] Handles, WindowAssignment Original,
        string TargetDisplayId, DateTime ExpiresAt);

    public void MoveForegroundRelative(int delta)
    {
        long handle = NativeWindows.ForegroundWindow;
        if (handle == 0) return;
        var assignment = Catalog.GetAssignment(handle);
        if (assignment is null) return;
        var display = Catalog.Snapshot().First(d => d.Id == assignment.DisplayId);
        int index = display.Desktops.ToList().FindIndex(d => d.Id == assignment.DesktopId);
        int next = ((index + delta) % display.Desktops.Count + display.Desktops.Count) % display.Desktops.Count;
        MoveWindow(handle, display.Desktops[next].Id);
    }

    public void ActivateWindow(long handle)
    {
        var assignment = Catalog.GetAssignment(handle);
        if (assignment is null) return;
        Switch(assignment.DisplayId, assignment.DesktopId);
        NativeWindows.Activate(handle);
    }

    public void TogglePause()
    {
        Paused = !Paused;
        ApplyVisibility();
        Changed?.Invoke();
    }

    public void RestoreAllAndPause()
    {
        Paused = true;
        _journal.RestoreAll();
        Changed?.Invoke();
    }
}

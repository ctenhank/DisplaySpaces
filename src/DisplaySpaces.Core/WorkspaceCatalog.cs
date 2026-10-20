namespace DisplaySpaces.Core;

public sealed record DisplayDescriptor(string Id, string Name, bool IsPrimary = false);

public sealed record WindowAssignment(long WindowId, string DisplayId, Guid DesktopId);

public sealed record DesktopSnapshot(Guid Id, string Name, IReadOnlyList<long> WindowIds, int? DefaultNumber = null);

public sealed record DisplaySnapshot(
    string Id,
    string Name,
    bool IsPrimary,
    Guid ActiveDesktopId,
    IReadOnlyList<DesktopSnapshot> Desktops);

/// <summary>
/// Owns logical desktops independently for each display. It contains no platform calls;
/// the window manager applies the visibility represented by this catalog.
/// </summary>
public sealed class WorkspaceCatalog
{
    private readonly object _sync = new();
    private readonly Dictionary<string, DisplayState> _displays = new(StringComparer.Ordinal);
    private readonly Dictionary<long, WindowAssignment> _windows = [];
    private List<string> _displayOrder = [];

    /// <summary>
    /// Reconciles a complete display topology. Windows from removed displays go to the
    /// active desktop of the remaining primary display, or the first display if none is primary.
    /// Invalid topologies, including an empty one, leave the catalog unchanged.
    /// </summary>
    public void SynchronizeDisplays(IEnumerable<DisplayDescriptor> displays)
    {
        ArgumentNullException.ThrowIfNull(displays);
        List<DisplayDescriptor> incoming = [];
        HashSet<string> ids = new(StringComparer.Ordinal);
        foreach (var display in displays)
        {
            if (display is null)
                throw new ArgumentException("A display descriptor cannot be null.", nameof(displays));
            ValidateText(display.Id, nameof(displays));
            ValidateText(display.Name, nameof(displays));
            if (!ids.Add(display.Id))
                throw new ArgumentException("Display IDs must be unique.", nameof(displays));
            incoming.Add(display);
        }

        if (incoming.Count == 0)
            throw new ArgumentException("At least one display is required.", nameof(displays));

        lock (_sync)
        {
            foreach (var descriptor in incoming)
            {
                if (_displays.TryGetValue(descriptor.Id, out var existing))
                {
                    existing.Name = descriptor.Name;
                    existing.IsPrimary = descriptor.IsPrimary;
                }
                else
                {
                    _displays.Add(descriptor.Id, new DisplayState(descriptor));
                }
            }

            var destinationDescriptor = incoming.FirstOrDefault(d => d.IsPrimary) ?? incoming[0];
            var destination = _displays[destinationDescriptor.Id];
            foreach (var assignment in _windows.Values.ToArray())
            {
                if (!ids.Contains(assignment.DisplayId))
                {
                    _windows[assignment.WindowId] = assignment with
                    {
                        DisplayId = destination.Id,
                        DesktopId = destination.ActiveDesktopId
                    };
                }
            }

            foreach (var removedId in _displays.Keys.Where(id => !ids.Contains(id)).ToArray())
                _displays.Remove(removedId);

            _displayOrder = incoming.Select(d => d.Id).ToList();
        }
    }

    /// <summary>Tracks a new window on the active desktop; rediscovery preserves its assignment.</summary>
    public void TrackWindow(long windowId, string displayId)
    {
        ValidateWindowId(windowId);
        lock (_sync)
        {
            var display = GetDisplay(displayId);
            _windows.TryAdd(windowId, new WindowAssignment(windowId, display.Id, display.ActiveDesktopId));
        }
    }

    /// <summary>Moves a tracked window to another display's active desktop. Same-display moves do nothing.</summary>
    public void AssignToDisplay(long windowId, string displayId)
    {
        ValidateWindowId(windowId);
        lock (_sync)
        {
            var destination = GetDisplay(displayId);
            var assignment = GetTrackedWindow(windowId);
            if (assignment.DisplayId != destination.Id)
            {
                _windows[windowId] = assignment with
                {
                    DisplayId = destination.Id,
                    DesktopId = destination.ActiveDesktopId
                };
            }
        }
    }

    /// <summary>Forgets a closed window. Unknown nonzero IDs are harmless.</summary>
    public void ForgetWindow(long windowId)
    {
        ValidateWindowId(windowId);
        lock (_sync)
            _windows.Remove(windowId);
    }

    /// <summary>Adds an inactive desktop and returns its stable ID.</summary>
    public Guid AddDesktop(string displayId, string? name = null)
    {
        if (name is not null)
            ValidateText(name, nameof(name));
        lock (_sync)
        {
            var display = GetDisplay(displayId);
            int? number = name is null ? display.NextDefaultNumber() : null;
            var desktop = new DesktopState(Guid.NewGuid(), name?.Trim() ?? $"Desktop {number}", number);
            display.Desktops.Add(desktop);
            return desktop.Id;
        }
    }

    public void RenameDesktop(string displayId, Guid desktopId, string name)
    {
        ValidateText(name, nameof(name));
        lock (_sync)
        {
            var desktop = GetDesktop(GetDisplay(displayId), desktopId);
            desktop.Name = name.Trim();
            desktop.DefaultNumber = null;
        }
    }

    /// <summary>
    /// Inserts an existing desktop at its final zero-based position. Its identity, name,
    /// windows, and active selection are preserved; other displays are not reordered.
    /// </summary>
    public bool ReorderDesktop(string displayId, Guid desktopId, int targetIndex)
    {
        lock (_sync)
        {
            var display = GetDisplay(displayId);
            var desktop = GetDesktop(display, desktopId);
            if (targetIndex < 0 || targetIndex >= display.Desktops.Count)
                throw new ArgumentOutOfRangeException(nameof(targetIndex));

            var sourceIndex = display.Desktops.IndexOf(desktop);
            if (sourceIndex == targetIndex) return false;
            display.Desktops.RemoveAt(sourceIndex);
            display.Desktops.Insert(targetIndex, desktop);
            return true;
        }
    }

    /// <summary>
    /// Removes a desktop and moves its windows to the preceding desktop, or the next
    /// desktop when removing the first. The last desktop cannot be removed.
    /// </summary>
    public void RemoveDesktop(string displayId, Guid desktopId)
    {
        lock (_sync)
        {
            var display = GetDisplay(displayId);
            var desktop = GetDesktop(display, desktopId);
            if (display.Desktops.Count == 1)
                throw new InvalidOperationException("A display must retain at least one desktop.");

            var index = display.Desktops.IndexOf(desktop);
            var destination = display.Desktops[index == 0 ? 1 : index - 1];
            foreach (var assignment in _windows.Values.Where(w =>
                         w.DisplayId == display.Id && w.DesktopId == desktopId).ToArray())
            {
                _windows[assignment.WindowId] = assignment with { DesktopId = destination.Id };
            }

            if (display.ActiveDesktopId == desktopId)
                display.ActiveDesktopId = destination.Id;
            display.Desktops.RemoveAt(index);
        }
    }

    /// <summary>
    /// Selects a desktop on one display, or its ordinal position on every display.
    /// Linked switching adds missing empty desktops without changing existing assignments.
    /// </summary>
    public void SwitchToDesktop(string displayId, Guid desktopId,
        DisplaySwitchingMode mode = DisplaySwitchingMode.Independent)
    {
        lock (_sync)
        {
            ValidateSwitchingMode(mode);
            var display = GetDisplay(displayId);
            var desktop = GetDesktop(display, desktopId);
            SwitchToIndex(display, display.Desktops.IndexOf(desktop), mode);
        }
    }

    /// <summary>
    /// Switches by a signed offset, optionally stopping at the first or last desktop.
    /// Linked switching uses the source display's active position and the longest desktop list.
    /// A request that leaves that position unchanged does not synchronize other displays.
    /// </summary>
    public void SwitchRelative(string displayId, int offset, bool wrapAround = true,
        DisplaySwitchingMode mode = DisplaySwitchingMode.Independent)
    {
        lock (_sync)
        {
            ValidateSwitchingMode(mode);
            var display = GetDisplay(displayId);
            var index = display.Desktops.FindIndex(d => d.Id == display.ActiveDesktopId);
            var count = mode == DisplaySwitchingMode.Linked
                ? _displays.Values.Max(d => d.Desktops.Count)
                : display.Desktops.Count;
            long requested = (long)index + offset;
            var target = wrapAround
                ? (int)((requested % count + count) % count)
                : (int)Math.Clamp(requested, 0L, count - 1L);
            if (target == index) return;
            SwitchToIndex(display, target, mode);
        }
    }

    // Called only while holding _sync, after validating the complete request.
    private void SwitchToIndex(DisplayState source, int index, DisplaySwitchingMode mode)
    {
        if (mode == DisplaySwitchingMode.Independent)
        {
            source.ActiveDesktopId = source.Desktops[index].Id;
            return;
        }

        foreach (var display in _displays.Values)
        {
            while (display.Desktops.Count <= index)
            {
                int number = display.NextDefaultNumber();
                display.Desktops.Add(new DesktopState(Guid.NewGuid(), $"Desktop {number}", number));
            }
            display.ActiveDesktopId = display.Desktops[index].Id;
        }
    }

    private static void ValidateSwitchingMode(DisplaySwitchingMode mode)
    {
        if (!Enum.IsDefined(mode))
            throw new ArgumentOutOfRangeException(nameof(mode));
    }

    /// <summary>Moves a tracked window to a desktop belonging to its current display.</summary>
    public void MoveWindowToDesktop(long windowId, Guid desktopId)
    {
        ValidateWindowId(windowId);
        lock (_sync)
        {
            var assignment = GetTrackedWindow(windowId);
            var destination = GetDesktop(_displays[assignment.DisplayId], desktopId);
            _windows[windowId] = assignment with { DesktopId = destination.Id };
        }
    }

    /// <summary>
    /// Moves an owner group to a specific desktop on any display in one catalog mutation.
    /// Every window and destination is validated before changing any assignment.
    /// </summary>
    public void MoveWindowsToDisplay(IEnumerable<long> windowIds, string displayId, Guid desktopId)
    {
        ArgumentNullException.ThrowIfNull(windowIds);
        var ids = windowIds.Distinct().ToArray();
        foreach (var id in ids) ValidateWindowId(id);
        lock (_sync)
        {
            var display = GetDisplay(displayId);
            var desktop = GetDesktop(display, desktopId);
            var assignments = ids.Select(GetTrackedWindow).ToArray();
            foreach (var assignment in assignments)
                _windows[assignment.WindowId] = assignment with { DisplayId = display.Id, DesktopId = desktop.Id };
        }
    }

    public WindowAssignment? GetAssignment(long windowId)
    {
        ValidateWindowId(windowId);
        lock (_sync)
            return _windows.GetValueOrDefault(windowId);
    }

    /// <summary>Returns logical workspace visibility; unknown windows return false.</summary>
    public bool IsWindowVisible(long windowId)
    {
        ValidateWindowId(windowId);
        lock (_sync)
        {
            return _windows.TryGetValue(windowId, out var assignment)
                   && _displays[assignment.DisplayId].ActiveDesktopId == assignment.DesktopId;
        }
    }

    /// <summary>Returns a detached, read-only snapshot in the latest display topology order.</summary>
    public IReadOnlyList<DisplaySnapshot> Snapshot()
    {
        lock (_sync)
        {
            return _displayOrder.Select(id =>
            {
                var display = _displays[id];
                var desktops = display.Desktops.Select(desktop => new DesktopSnapshot(
                    desktop.Id,
                    desktop.Name,
                    Array.AsReadOnly(_windows.Values
                        .Where(w => w.DisplayId == display.Id && w.DesktopId == desktop.Id)
                        .Select(w => w.WindowId)
                        .Order()
                        .ToArray()), desktop.DefaultNumber)).ToArray();
                return new DisplaySnapshot(display.Id, display.Name, display.IsPrimary,
                    display.ActiveDesktopId, Array.AsReadOnly(desktops));
            }).ToList().AsReadOnly();
        }
    }

    private DisplayState GetDisplay(string displayId)
    {
        ValidateText(displayId, nameof(displayId));
        return _displays.TryGetValue(displayId, out var display)
            ? display
            : throw new ArgumentException("The display is not registered.", nameof(displayId));
    }

    private static DesktopState GetDesktop(DisplayState display, Guid desktopId)
    {
        return display.Desktops.Find(d => d.Id == desktopId)
            ?? throw new ArgumentException("The desktop does not belong to this display.", nameof(desktopId));
    }

    private WindowAssignment GetTrackedWindow(long windowId)
    {
        return _windows.TryGetValue(windowId, out var assignment)
            ? assignment
            : throw new ArgumentException("The window is not tracked.", nameof(windowId));
    }

    private static void ValidateText(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
    }

    private static void ValidateWindowId(long windowId)
    {
        // HWNDs are signed pointer values when represented by IntPtr.ToInt64().
        if (windowId == 0)
            throw new ArgumentOutOfRangeException(nameof(windowId), "A window ID cannot be zero.");
    }

    private sealed class DisplayState
    {
        private int _nextDefaultNumber = 2;

        public DisplayState(DisplayDescriptor descriptor)
        {
            Id = descriptor.Id;
            Name = descriptor.Name;
            IsPrimary = descriptor.IsPrimary;
            var initialDesktop = new DesktopState(Guid.NewGuid(), "Desktop 1", 1);
            Desktops = [initialDesktop];
            ActiveDesktopId = initialDesktop.Id;
        }

        public string Id { get; }
        public string Name { get; set; }
        public bool IsPrimary { get; set; }
        public Guid ActiveDesktopId { get; set; }
        public List<DesktopState> Desktops { get; }

        public int NextDefaultNumber()
        {
            int candidate;
            do
            {
                candidate = _nextDefaultNumber++;
            } while (Desktops.Any(d => d.Name == $"Desktop {candidate}"));
            return candidate;
        }
    }

    private sealed class DesktopState(Guid id, string name, int? defaultNumber)
    {
        public Guid Id { get; } = id;
        public string Name { get; set; } = name;
        public int? DefaultNumber { get; set; } = defaultNumber;
    }
}

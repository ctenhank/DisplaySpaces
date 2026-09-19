using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using BetterVirtualDesktop.App.Native;

namespace BetterVirtualDesktop.App.Services;

public sealed record RecoveryEntry(long Handle, int ProcessId, long ProcessStartTicks, string ClassName,
    bool Minimized, bool Maximized, long Marker);
public sealed record RecoveryDocument(int OwnerProcessId, long OwnerStartTicks, List<RecoveryEntry> Windows);

/// <summary>Write-ahead journal: an external window is never hidden before its recovery record is durable.</summary>
public sealed class RecoveryJournal
{
    // Preserve earlier recovery journals; renaming must not strand hidden windows.
    public static string DirectoryPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "IndependentVirtualDesktop", "recovery");
    private readonly Dictionary<long, RecoveryEntry> _entries = new();
    private readonly int _ownerPid = Environment.ProcessId;
    private readonly long _ownerStart = Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    public string FilePath { get; }
    public IReadOnlyCollection<RecoveryEntry> Entries => _entries.Values;

    public RecoveryJournal(string? filePath = null)
    {
        FilePath = filePath ?? Path.Combine(DirectoryPath, $"{_ownerPid}-{_ownerStart}.json");
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        Save();
    }

    public bool Contains(long handle) => _entries.ContainsKey(handle);
    public bool PrepareHide(NativeWindowInfo window)
    {
        // Hotkey actions can run between enumerations. Never apply a stale snapshot to a
        // replacement window that happened to receive the same recycled HWND.
        if (!NativeWindows.IsSameWindow(window.Handle, window.ProcessId,
                window.ProcessStartTicks, window.ClassName)) return false;
        if (_entries.TryGetValue(window.Handle, out var existing)) return Matches(existing);
        if (!NativeWindows.IsVisible(window.Handle)) return false;
        long marker = RandomNumberGenerator.GetInt32(1, int.MaxValue);
        var entry = new RecoveryEntry(window.Handle, window.ProcessId, window.ProcessStartTicks,
            window.ClassName, NativeWindows.IsMinimized(window.Handle), NativeWindows.IsMaximized(window.Handle), marker);
        _entries.Add(window.Handle, entry);
        try { Save(); }
        catch { _entries.Remove(window.Handle); throw; }
        if (NativeWindows.SetRecoveryMarker(window.Handle, marker)) return true;
        _entries.Remove(window.Handle);
        Save();
        return false;
    }

    public void Restore(long handle)
    {
        if (!_entries.TryGetValue(handle, out var entry)) return;
        if (!Matches(entry)) { _entries.Remove(handle); Save(); return; }
        NativeWindows.Show(handle, entry.Minimized, entry.Maximized);
        // ShowWindowAsync reports queuing only. Keep the journal until a later visibility check.
    }

    public void ConfirmRestored(long handle)
    {
        if (!_entries.TryGetValue(handle, out var entry)) return;
        if (Matches(entry) && !NativeWindows.IsVisible(handle)) return;
        if (Matches(entry)) NativeWindows.ClearRecoveryMarker(handle);
        _entries.Remove(handle);
        Save();
    }

    public void RestoreAll()
    {
        foreach (var handle in _entries.Keys.ToArray())
        {
            try { Restore(handle); }
            catch (Exception error) { AppLog.Write(error); }
        }
    }

    public void PruneClosed()
    {
        bool changed = false;
        foreach (var entry in _entries.Values.ToArray())
            if (!Matches(entry)) { _entries.Remove(entry.Handle); changed = true; }
        if (changed) Save();
    }

    public void DeleteIfEmpty()
    {
        if (_entries.Count == 0 && File.Exists(FilePath)) File.Delete(FilePath);
    }

    public static bool Matches(RecoveryEntry entry) =>
        NativeWindows.IsSameWindow(entry.Handle, entry.ProcessId, entry.ProcessStartTicks, entry.ClassName)
        && NativeWindows.GetRecoveryMarker(entry.Handle) == entry.Marker;

    private void Save() => WriteDocument(FilePath, new(_ownerPid, _ownerStart, _entries.Values.ToList()));

    private static void WriteDocument(string path, RecoveryDocument document)
    {
        string temp = path + ".tmp";
        using (var stream = new FileStream(temp, FileMode.Create, FileAccess.Write, FileShare.None,
            4096, FileOptions.WriteThrough))
        {
            JsonSerializer.Serialize(stream, document);
            stream.Flush(true);
        }
        File.Move(temp, path, true);
    }

    public static bool OwnerAlive(int pid, long startTicks)
    {
        try { using var process = Process.GetProcessById(pid); return process.StartTime.ToUniversalTime().Ticks == startTicks && !process.HasExited; }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; } // Unknown owner: do not take over its journal.
    }

    public static void RecoverOrphans()
    {
        Directory.CreateDirectory(DirectoryPath);
        foreach (string path in Directory.EnumerateFiles(DirectoryPath, "*.json")) RecoverFile(path);
    }

    public static bool RecoverFile(string path)
    {
        try
        {
            if (!File.Exists(path)) return true;
            var document = JsonSerializer.Deserialize<RecoveryDocument>(File.ReadAllText(path));
            if (document is null || OwnerAlive(document.OwnerProcessId, document.OwnerStartTicks)) return false;
            var remaining = document.Windows.Where(Matches).ToList();
            for (int attempt = 0; attempt < 20 && remaining.Count > 0; attempt++)
            {
                foreach (var entry in remaining) NativeWindows.Show(entry.Handle, entry.Minimized, entry.Maximized);
                Thread.Sleep(100);
                remaining.RemoveAll(entry =>
                {
                    if (!Matches(entry)) return true;
                    if (!NativeWindows.IsVisible(entry.Handle)) return false;
                    NativeWindows.ClearRecoveryMarker(entry.Handle);
                    return true;
                });
            }
            // Preserve unrecovered records so a later --recover can retry a hung application's window.
            if (remaining.Count == 0) File.Delete(path);
            else WriteDocument(path, document with { Windows = remaining });
            return remaining.Count == 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { AppLog.Write(ex); return false; }
    }
}

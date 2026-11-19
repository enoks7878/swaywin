using System.Diagnostics;
using System.Text.Json;

namespace SwayWin;

internal sealed class WindowRestoreData
{
    public long Handle { get; set; }
    public int ProcessId { get; set; }
    public long ProcessStartedUtcTicks { get; set; }
    public string WindowClass { get; set; } = "";
    public long Style { get; set; }
    public Native.WindowPlacement Placement { get; set; }
    public bool WasVisible { get; set; }
    public int DarkMode { get; set; }
    public int Corners { get; set; }
    public int BorderColor { get; set; }
    public int CaptionColor { get; set; }
    public int TextColor { get; set; }
    public bool RestorePlacement { get; set; } = true;
    internal Task BeforeRestore { get; set; } = Task.CompletedTask;
    internal Task RestorationCompletion { get; private set; } = Task.CompletedTask;

    internal Task<bool> BeginRestore()
    {
        lock (this)
        {
            Task previous = RestorationCompletion;
            Task<bool> attempt = Task.Run(() =>
            {
                try
                {
                    if (!previous.Wait(TimeSpan.FromMilliseconds(1500))) return false;
                    return Restore();
                }
                catch (AggregateException exception) { AppLog.Write(exception.ToString()); return false; }
            });
            RestorationCompletion = attempt;
            return attempt;
        }
    }

    internal static WindowRestoreData Capture(nint hwnd)
    {
        int processId = (int)Native.GetProcessId(hwnd);
        using Process process = Process.GetProcessById(processId);
        return new()
        {
            Handle = hwnd.ToInt64(), ProcessId = processId, ProcessStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks,
            WindowClass = Native.GetClass(hwnd),
            Style = Native.GetStyle(hwnd), Placement = Native.GetPlacement(hwnd), WasVisible = Native.IsWindowVisible(hwnd),
            DarkMode = Native.DwmGetInt(hwnd, Native.DwmwaUseImmersiveDarkMode, 0),
            Corners = Native.DwmGetInt(hwnd, Native.DwmwaWindowCornerPreference, 0),
            BorderColor = Native.DwmGetInt(hwnd, Native.DwmwaBorderColor, Native.DwmColorDefault),
            CaptionColor = Native.DwmGetInt(hwnd, Native.DwmwaCaptionColor, Native.DwmColorDefault),
            TextColor = Native.DwmGetInt(hwnd, Native.DwmwaTextColor, Native.DwmColorDefault)
        };
    }

    internal bool? IsCurrentWindow()
    {
        nint hwnd = (nint)Handle;
        if (!Native.IsWindow(hwnd) || Native.GetProcessId(hwnd) != ProcessId ||
            (WindowClass.Length > 0 && Native.GetClass(hwnd) != WindowClass)) return false;
        try
        {
            using Process process = Process.GetProcessById(ProcessId);
            return process.StartTime.ToUniversalTime().Ticks == ProcessStartedUtcTicks;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return null; }
    }

    internal bool Restore(bool? restorePlacement = null)
    {
        try
        {
            if (!BeforeRestore.Wait(TimeSpan.FromMilliseconds(1500)))
            { AppLog.Write($"Restore {Handle}: native worker has not drained; record retained."); return false; }
        }
        catch (AggregateException exception) { AppLog.Write(exception.ToString()); return false; }
        bool? identity = IsCurrentWindow();
        if (identity == false) return true;
        if (identity is null) return false;
        nint hwnd = (nint)Handle;
        bool placement = restorePlacement ?? RestorePlacement;
        bool complete = true;
        if (placement && WasVisible && !Native.IsWindowVisible(hwnd))
            complete = Native.ShowWindowAsync(hwnd, Native.SwShowna);
        if (!Native.IsResponsiveWindow(hwnd))
        {
            AppLog.Write($"Restore {Handle}: deferred while application is unresponsive.");
            return false;
        }
        try
        {
            const long frame = Native.WsCaption | Native.WsThickframe;
            long current = Native.GetStyle(hwnd);
            long style = (current & ~frame) | (Style & frame);
            if (style != current) Native.SetStyle(hwnd, style);
        }
        catch (System.ComponentModel.Win32Exception exception)
        {
            complete = false;
            AppLog.Write($"Restore frame {Handle}: {exception.Message}");
        }
        try
        {
            Native.DwmSetInt(hwnd, Native.DwmwaUseImmersiveDarkMode, DarkMode);
            Native.DwmSetInt(hwnd, Native.DwmwaWindowCornerPreference, Corners);
            Native.DwmSetInt(hwnd, Native.DwmwaBorderColor, BorderColor);
            Native.DwmSetInt(hwnd, Native.DwmwaCaptionColor, CaptionColor);
            Native.DwmSetInt(hwnd, Native.DwmwaTextColor, TextColor);
            if (placement)
            {
                Native.WindowPlacement currentPlacement = Native.GetPlacement(hwnd);
                if (currentPlacement.ShowCmd != Placement.ShowCmd || currentPlacement.NormalPosition.ToRectangle() != Placement.NormalPosition.ToRectangle())
                    complete &= Native.SetPlacement(hwnd, Placement);
                if (WasVisible && !Native.IsWindowVisible(hwnd) && Placement.ShowCmd != Native.SwShowminimized)
                    complete &= Native.ShowWindowAsync(hwnd, Native.SwShowna);
            }
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            AppLog.Write($"Restore {Handle}: {exception.Message}");
            complete = false;
        }
        return complete;
    }
}

internal sealed class RecoveryJournal
{
    public int OwnerProcessId { get; set; }
    public long OwnerStartedUtcTicks { get; set; }
    public List<WindowRestoreData> Windows { get; set; } = [];
}

internal static class Recovery
{
    internal static string DataDirectory => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SwayWin");
    internal static string JournalPath => Path.Combine(DataDirectory, $"recovery-{Process.GetCurrentProcess().SessionId}.json");
    private static readonly JsonSerializerOptions JsonOptions = new() { IncludeFields = true, WriteIndented = false };

    internal static void Save(IEnumerable<WindowRestoreData> windows)
    {
        Directory.CreateDirectory(DataDirectory);
        using Process process = Process.GetCurrentProcess();
        RecoveryJournal journal = new()
        {
            OwnerProcessId = process.Id, OwnerStartedUtcTicks = process.StartTime.ToUniversalTime().Ticks, Windows = windows.ToList()
        };
        WriteJournal(JournalPath, journal);
    }

    private static void WriteJournal(string path, RecoveryJournal journal)
    {
        string temporary = path + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(journal, JsonOptions));
        File.Move(temporary, path, true);
    }

    internal static void Clear()
    {
        try { File.Delete(JournalPath); }
        catch (IOException exception) { AppLog.Write(exception.Message); }
    }

    internal static int RestoreJournal(string path, bool requireDeadOwner = true)
    {
        if (!File.Exists(path)) return 0;
        RecoveryJournal? journal;
        try { journal = JsonSerializer.Deserialize<RecoveryJournal>(File.ReadAllText(path), JsonOptions); }
        catch (Exception exception) when (exception is IOException or JsonException)
        { AppLog.Write($"Cannot read recovery journal: {exception.Message}"); return -1; }
        if (journal is null) return -1;
        if (requireDeadOwner && IsSameProcessAlive(journal.OwnerProcessId, journal.OwnerStartedUtcTicks)) return -1;
        (List<WindowRestoreData> remaining, int restored) = RestoreWindows(journal.Windows);
        try
        {
            if (remaining.Count == 0) File.Delete(path);
            else { journal.Windows = remaining; WriteJournal(path, journal); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { AppLog.Write($"Recovery journal: {exception.Message}"); return -1; }
        return remaining.Count == 0 ? restored : -1;
    }

    internal static (List<WindowRestoreData> Remaining, int Restored) RestoreWindows(IEnumerable<WindowRestoreData> windows)
    {
        var attempts = windows.GroupBy(window => window.Handle).Select(group => group.First()).Select(window =>
            (Window: window, Live: window.IsCurrentWindow() != false, Task: window.BeginRestore())).ToArray();
        try { Task.WaitAll(attempts.Select(attempt => attempt.Task).ToArray(), TimeSpan.FromSeconds(2)); }
        catch (AggregateException exception) { AppLog.Write(exception.ToString()); }
        List<WindowRestoreData> remaining = attempts.Where(attempt => !attempt.Task.IsCompletedSuccessfully || !attempt.Task.Result)
            .Select(attempt => attempt.Window).ToList();
        int restored = attempts.Count(attempt => attempt.Live && attempt.Task.IsCompletedSuccessfully && attempt.Task.Result);
        return (remaining, restored);
    }

    internal static void StartWatchdog()
    {
        string executable = Environment.ProcessPath ?? throw new InvalidOperationException("Cannot locate SwayWin executable.");
        ProcessStartInfo info = new(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            info.ArgumentList.Add(typeof(Program).Assembly.Location);
        info.ArgumentList.Add("--watchdog");
        info.ArgumentList.Add(Environment.ProcessId.ToString());
        info.ArgumentList.Add(JournalPath);
        using Process? guardian = Process.Start(info);
        if (guardian is null) throw new InvalidOperationException("Could not start window-recovery watchdog.");
    }

    internal static int RunWatchdog(int ownerProcessId, string path)
    {
        // The journal is written before any external window is hidden or redecorated.
        try
        {
            using Process owner = Process.GetProcessById(ownerProcessId);
            owner.WaitForExit();
        }
        catch (ArgumentException) { }
        using Mutex gate = new(false, CommandServer.MutexName);
        bool acquired;
        try { acquired = gate.WaitOne(TimeSpan.FromSeconds(2)); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired) return 0;
        try { return RestoreJournal(path) < 0 ? 1 : 0; }
        finally { gate.ReleaseMutex(); }
    }

    private static bool IsSameProcessAlive(int id, long started)
    {
        try
        {
            using Process process = Process.GetProcessById(id);
            return !process.HasExited && process.StartTime.ToUniversalTime().Ticks == started;
        }
        catch (ArgumentException) { return false; }
        catch (InvalidOperationException) { return false; }
        catch (System.ComponentModel.Win32Exception) { return true; }
    }
}

internal static class AppLog
{
    private static readonly object Gate = new();
    internal static string PathName => Path.Combine(Recovery.DataDirectory, "swaywin.log");

    internal static void Write(string message)
    {
        lock (Gate)
        {
            try
            {
                Directory.CreateDirectory(Recovery.DataDirectory);
                if (File.Exists(PathName) && new FileInfo(PathName).Length > 2_000_000)
                    File.Move(PathName, PathName + ".previous", true);
                File.AppendAllText(PathName, $"{DateTime.Now:yyyy-MM-dd HH:mm:ss} {message}{Environment.NewLine}");
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}

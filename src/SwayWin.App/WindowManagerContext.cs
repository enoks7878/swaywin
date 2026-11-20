using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using SwayWin.Core.Configuration;
using SwayWin.Core.Layout;

namespace SwayWin;

internal sealed class WindowManagerContext : ApplicationContext, IInputHost
{
    private sealed class ManagedWindow(WindowRestoreData original, string processName, string windowClass)
    {
        internal WindowRestoreData Original { get; } = original;
        internal string ProcessName { get; } = processName;
        internal string WindowClass { get; } = windowClass;
        internal bool Hidden;
        internal long IgnoreEventsUntil;
        internal Rectangle NativeBounds;
        internal bool Limited;
        internal bool Deferred;
        internal int GeometryRetries;
        internal Rectangle TargetBounds;
        internal long RequestVersion;
        internal int VisibilityInFlight;
        internal long VisibilityStart;
        internal long VisibilityEnd;
        internal NativeWorkQueue Worker { get; set; } = new();
        internal HashSet<int> AppliedRules { get; } = [];
    }

    private static readonly HashSet<string> ShellClasses = new(StringComparer.OrdinalIgnoreCase)
    {
        "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "NotifyIconOverflowWindow",
        "MultitaskingViewFrame", "TaskListThumbnailWnd", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland"
    };
    private static readonly HashSet<string> ShellProcesses = new(StringComparer.OrdinalIgnoreCase)
    { "StartMenuExperienceHost", "ShellExperienceHost", "SearchHost", "SearchApp", "TextInputHost", "LockApp", "Widgets" };
    private readonly AppOptions options;
    private readonly Control dispatcher = new();
    private readonly Dictionary<nint, ManagedWindow> managed = [];
    private readonly Dictionary<nint, WindowRestoreData> pendingRecovery = [];
    private readonly HashSet<nint> pendingRestores = [];
    private readonly Dictionary<nint, WindowRestoreData> rejected = [];
    private readonly Dictionary<string, WorkspaceBar> bars = new(StringComparer.Ordinal);
    private readonly Dictionary<long, DecorationForm> borders = [];
    private readonly Dictionary<long, DecorationForm> headers = [];
    private readonly List<nint> eventHooks = [];
    private readonly Native.WinEventProc eventCallback;
    private readonly InputHooks input;
    private readonly DesktopActions desktop;
    private readonly NotifyIcon tray;
    private readonly ToolStripMenuItem pauseItem;
    private readonly System.Windows.Forms.Timer maintenance = new() { Interval = 1000 };
    private readonly System.Windows.Forms.Timer hover = new() { Interval = 75 };
    private readonly System.Windows.Forms.Timer layoutTimer = new() { Interval = 16 };
    private readonly WindowManagerState state;
    private CommandServer? commands;
    private volatile bool paused;
    private volatile bool stopping;
    private bool initializing;
    private bool applying;
    private bool wantFocus;
    private bool displayUpdatePending;
    private bool handlingIpc;
    private bool exitAfterReply;
    private uint focusIntentTime;
    private bool hasFocusIntent;
    private Native.Point previousPointer;
    private readonly System.Windows.Forms.Timer focusTimer = new() { Interval = 40 };
    private nint focusTarget;
    private long focusDeadline;
    private long foregroundSyncBlockedUntil;

    internal WindowManagerContext(SwayConfig config, AppOptions options)
    {
        Config = config;
        this.options = options;
        state = new(config.Settings, ReadOutputs());
        _ = dispatcher.Handle;
        input = new(this);
        eventCallback = WinEvent;
        desktop = new(() => ToRectangle(state.ActiveOutput.Bounds), RestoreFocus, Notify, Path.GetPathRoot(config.SourcePath) ?? "");
        ContextMenuStrip menu = new() { BackColor = Theme.Surface, ForeColor = Theme.Text };
        menu.Items.Add("Reload configuration", null, (_, _) => ExecuteCommand("reload"));
        menu.Items.Add("Open Windows overrides", null, (_, _) => OpenOverrides());
        menu.Items.Add("Open log", null, (_, _) => Process.Start(new ProcessStartInfo("notepad.exe", AppLog.PathName) { UseShellExecute = true }));
        pauseItem = new("Pause tiling", null, (_, _) => TogglePause());
        menu.Items.Add(pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit and restore windows", null, (_, _) => Stop());
        tray = new() { Icon = SystemIcons.Application, Text = "SwayWin", ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => desktop.TryExecute("swaywin-launcher");
        maintenance.Tick += (_, _) => Reconcile();
        hover.Tick += (_, _) => PointerFocus();
        focusTimer.Tick += (_, _) => RetryFocus();
        layoutTimer.Tick += (_, _) =>
        {
            layoutTimer.Stop();
            if (displayUpdatePending) { displayUpdatePending = false; UpdateDisplays(); }
            bool focus = wantFocus;
            wantFocus = false;
            ApplyLayout(focus);
        };
        SystemEvents.DisplaySettingsChanged += DisplayChanged;
        SystemEvents.UserPreferenceChanged += PreferencesChanged;
        Post(Start);
    }

    public SwayConfig Config { get; private set; }
    public string Mode { get; private set; } = "default";
    public bool SuspendBindings => paused || stopping || desktop.IsOverlayOpen;

    public void Post(Action action)
    {
        if (stopping || dispatcher.IsDisposed) return;
        try
        {
            dispatcher.BeginInvoke(() =>
            {
                if (stopping) return;
                try { action(); }
                catch (Exception exception) { Notify(exception.Message); AppLog.Write(exception.ToString()); }
            });
        }
        catch (InvalidOperationException) { }
    }

    public void RequestEmergencyExit() => Stop();

    private void Start()
    {
        initializing = true;
        try
        {
            nint foreground = Native.GetForegroundWindow();
            EnumerateExisting();
            if (managed.ContainsKey(foreground)) state.FocusWindow(foreground.ToInt64());
            SaveJournal();
            Recovery.StartWatchdog();
            CreateBars();
            UpdateDisplays();
            input.Start();
            HookEvent(Native.EventSystemForeground, Native.EventSystemForeground);
            HookEvent(Native.EventSystemMoveSizeEnd, Native.EventSystemMoveSizeEnd);
            HookEvent(Native.EventObjectDestroy, Native.EventObjectHide);
            HookEvent(Native.EventObjectLocationChange, Native.EventObjectNameChange);
            commands = new(HandleRequest, () => Post(() => { if (exitAfterReply) Stop(); }));
            maintenance.Start();
            hover.Start();
            initializing = false;
            ApplyLayout(false);
            foreach (string command in options.NoAutostart ? [] : Config.StartupCommands) ExecuteCommand("exec " + Config.Expand(command));
            foreach (ConfigDiagnostic diagnostic in Config.Diagnostics) AppLog.Write($"{diagnostic.Severity} {diagnostic.File}:{diagnostic.Line}: {diagnostic.Message}");
            foreach (string warning in input.Warnings) AppLog.Write(warning);
            tray.ShowBalloonTip(5000, "SwayWin", "Tiling is active. Exit: tray menu or Ctrl+Alt+Shift+F12. Original window positions are saved.", ToolTipIcon.Info);
        }
        catch (Exception exception)
        {
            AppLog.Write(exception.ToString());
            MessageBox.Show($"SwayWin could not start. Windows will be restored.\n\n{exception.Message}", "SwayWin", MessageBoxButtons.OK, MessageBoxIcon.Error);
            Stop();
        }
        finally { initializing = false; }
    }

    private void HookEvent(uint minimum, uint maximum)
    {
        nint hook = Native.SetWinEventHook(minimum, maximum, 0, eventCallback, 0, 0, Native.WineventOutofcontext | Native.WineventSkipownprocess);
        if (hook == 0) throw new Win32Exception("Could not install a window event hook.");
        eventHooks.Add(hook);
    }

    private void WinEvent(nint hook, uint eventType, nint hwnd, int objectId, int childId, uint eventThread, uint eventTime)
    {
        if (hwnd == 0 || objectId != Native.ObjidWindow || childId != 0 || stopping) return;
        Post(() => OnWindowEvent(eventType, hwnd, eventTime));
    }

    private void OnWindowEvent(uint type, nint hwnd, uint eventTime)
    {
        if (type == Native.EventSystemForeground && (Native.GetForegroundWindow() != hwnd ||
            hasFocusIntent && unchecked((int)(eventTime - focusIntentTime)) < 0)) return;
        if (type == Native.EventObjectDestroy)
        {
            ReleaseWindow(hwnd, false);
            rejected.Remove(hwnd);
            ScheduleLayout();
            return;
        }
        if (paused || desktop.IsOverlayOpen) return;
        if (!managed.TryGetValue(hwnd, out ManagedWindow? window))
        {
            if (type is Native.EventObjectShow or Native.EventSystemForeground) Adopt(hwnd, Native.GetForegroundWindow() == hwnd);
            if (type == Native.EventSystemForeground) ScheduleLayout();
            return;
        }
        if (type == Native.EventSystemForeground)
        {
            if (Native.GetForegroundWindow() != hwnd || !Native.IsWindowVisible(hwnd) || window.Hidden) return;
            // Windows hands activation to another window while the previous workspace is being hidden.
            // That is not a request to leave the workspace the user just selected.
            if (Environment.TickCount64 < foregroundSyncBlockedUntil &&
                state.Windows.TryGetValue(hwnd.ToInt64(), out WindowNode? node) &&
                state.WorkspaceOf(node) is { } workspace && workspace != state.CurrentWorkspace)
            { ScheduleLayout(); return; }
            state.FocusWindow(hwnd.ToInt64());
            ScheduleLayout();
        }
        else if (type == Native.EventObjectHide)
        {
            if (!window.Hidden && !Native.IsWindowVisible(hwnd))
            {
                long started = Interlocked.Read(ref window.VisibilityStart);
                long ended = Interlocked.Read(ref window.VisibilityEnd);
                bool managerEvent = started > 0 && unchecked((int)(eventTime - (uint)started)) >= 0 &&
                    unchecked((int)((uint)ended - eventTime)) >= 0;
                if (Volatile.Read(ref window.VisibilityInFlight) > 0 || window.Worker.IsBusy || managerEvent ||
                    Environment.TickCount64 <= window.IgnoreEventsUntil)
                    ScheduleLayout();
                else { ReleaseWindow(hwnd, true); ScheduleLayout(); }
            }
        }
        else if (type == Native.EventObjectShow)
        {
            if (window.Hidden && Environment.TickCount64 > window.IgnoreEventsUntil && Native.IsWindowVisible(hwnd) && Native.GetForegroundWindow() == hwnd)
                state.FocusWindow(hwnd.ToInt64());
            ScheduleLayout();
        }
        else if (type == Native.EventObjectNameChange)
        {
            state.Windows[hwnd.ToInt64()].Title = Native.GetText(hwnd);
            ApplyRules(hwnd);
            ScheduleLayout();
        }
        else if (type is Native.EventObjectLocationChange or Native.EventSystemMoveSizeEnd)
        {
            if (Environment.TickCount64 < window.IgnoreEventsUntil || applying) return;
            if (Native.IsIconic(hwnd) && !window.Hidden) { ReleaseWindow(hwnd, true); ScheduleLayout(); return; }
            Rectangle actual = Native.GetBounds(hwnd, true);
            if (actual == window.NativeBounds) return;
            WindowNode node = state.Windows[hwnd.ToInt64()];
            if (node.IsFloating && state.WorkspaceOf(node)?.FullscreenNode is null)
            {
                int border = Config.Settings.FloatingBorderWidth;
                actual.Inflate(border, border);
                state.SetFloatingBounds(node.WindowId, ToPixelRect(actual));
            }
            ScheduleLayout();
        }
    }

    private void EnumerateExisting()
    {
        Native.EnumWindows((hwnd, _) =>
        {
            try { Adopt(hwnd, false); }
            catch (Exception exception) { AppLog.Write($"Enumerating {hwnd}: {exception.Message}"); }
            return true;
        }, 0);
    }

    private bool ShouldManage(nint hwnd, out string processName, out string windowClass, out bool floating, out long? ownerId)
    {
        processName = ""; windowClass = ""; floating = false; ownerId = null;
        if (pendingRecovery.ContainsKey(hwnd)) return false;
        if (rejected.TryGetValue(hwnd, out WindowRestoreData? blocked))
        {
            if (blocked.IsCurrentWindow() != false) return false;
            rejected.Remove(hwnd);
        }
        if (!Native.IsWindow(hwnd) || !Native.IsWindowVisible(hwnd) || Native.IsIconic(hwnd)
            || Native.IsHungAppWindow(hwnd) || Native.GetProcessId(hwnd) == Environment.ProcessId || Native.GetAncestor(hwnd, Native.GaRoot) != hwnd) return false;
        long style = Native.GetStyle(hwnd);
        long extended = Native.GetExtendedStyle(hwnd);
        if ((style & Native.WsChild) != 0 || (extended & Native.WsExNoactivate) != 0 || Native.DwmGetInt(hwnd, Native.DwmwaCloaked, 0) != 0) return false;
        windowClass = Native.GetClass(hwnd);
        if (ShellClasses.Contains(windowClass) || Native.GetText(hwnd).Length == 0) return false;
        try
        {
            using Process process = Process.GetProcessById((int)Native.GetProcessId(hwnd));
            processName = process.ProcessName;
        }
        catch (Exception exception) when (exception is ArgumentException or InvalidOperationException or Win32Exception) { return false; }
        if (ShellProcesses.Contains(processName)) return false;
        nint owner = Native.GetWindow(hwnd, Native.GwOwner);
        if ((extended & Native.WsExToolwindow) != 0 && windowClass != "#32770") return false;
        if (owner != 0)
        {
            if (!managed.ContainsKey(owner) && windowClass != "#32770") return false;
            floating = true;
            if (managed.ContainsKey(owner)) ownerId = owner.ToInt64();
        }
        if (windowClass == "#32770" || (style & Native.WsThickframe) == 0) floating = true;
        return true;
    }

    private void Adopt(nint hwnd, bool focus)
    {
        if (managed.ContainsKey(hwnd) || paused || stopping) return;
        if (!ShouldManage(hwnd, out string processName, out string windowClass, out bool floating, out long? owner)) return;
        try
        {
            WindowRestoreData original = WindowRestoreData.Capture(hwnd);
            ManagedWindow record = new(original, processName, windowClass);
            WindowIdentity identity = new(hwnd.ToInt64(), Native.GetText(hwnd), windowClass, processName, floating);
            string? workspace = owner is long ownerHandle && state.Windows.TryGetValue(ownerHandle, out WindowNode? ownerWindow)
                ? state.WorkspaceOf(ownerWindow)?.Name : null;
            workspace ??= Config.Assignments.FirstOrDefault(rule => rule.Criteria.Matches(identity))?.Workspace;
            bool noFocus = Config.NoFocusRules.Any(criteria => criteria.Matches(identity));
            managed.Add(hwnd, record);
            state.AddWindow(hwnd.ToInt64(), identity.Title, ToPixelRect(Native.GetBounds(hwnd, true)), floating, workspace, focus && !noFocus, owner);
            // Persist the original native state before rules, styles, visibility or geometry can change.
            SaveJournal();
            ApplyRules(hwnd);
            if (!initializing) ScheduleLayout(focus && !noFocus && state.WorkspaceOf(state.Windows[hwnd.ToInt64()]) == state.CurrentWorkspace);
        }
        catch (Exception exception) when (exception is Win32Exception or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            AppLog.Write($"Cannot manage {hwnd}: {exception.Message}");
            ReleaseWindow(hwnd, true);
        }
    }

    private WindowIdentity Identity(WindowNode node)
    {
        ManagedWindow record = managed[(nint)node.WindowId];
        return new(node.Id, node.Title, record.WindowClass, record.ProcessName, node.IsFloating, state.WorkspaceOf(node)?.Name ?? "__scratchpad", node.Urgent);
    }

    private void ApplyRules(nint hwnd)
    {
        if (!managed.TryGetValue(hwnd, out ManagedWindow? record) || !state.Windows.TryGetValue(hwnd.ToInt64(), out WindowNode? window)) return;
        WindowIdentity identity = Identity(window);
        long? originalFocus = state.FocusedWindow?.WindowId;
        long? originalSelection = state.SelectedNode?.Id;
        string originalWorkspace = state.CurrentWorkspace.Name;
        string originalOutput = state.ActiveOutput.Name;
        bool changed = false;
        for (int i = 0; i < Config.WindowRules.Count; i++)
        {
            WindowRule rule = Config.WindowRules[i];
            if (record.AppliedRules.Contains(i) || !rule.Criteria.Matches(identity)) continue;
            changed = true;
            record.AppliedRules.Add(i);
            state.FocusWindow(window.WindowId);
            ExecuteSequence(rule.Command, false);
        }
        if (changed)
        {
            state.FocusOutput(originalOutput);
            state.SwitchWorkspace(originalWorkspace, true);
            if (originalSelection is long nodeId && state.FindNode(nodeId) is not null) state.FocusNodeById(nodeId);
            else if (originalFocus is long focused) state.FocusWindow(focused);
        }
    }

    private void ReleaseWindow(nint hwnd, bool restoreStyle, bool restorePlacement = false)
    {
        if (!managed.Remove(hwnd, out ManagedWindow? window)) return;
        Interlocked.Increment(ref window.RequestVersion);
        window.Original.BeforeRestore = Task.WhenAll(window.Original.BeforeRestore, window.Worker.Cancel());
        if (restoreStyle && window.Original.IsCurrentWindow() != false)
        {
            window.Original.RestorePlacement = restorePlacement;
            pendingRecovery[hwnd] = window.Original;
        }
        state.RemoveWindow(hwnd.ToInt64());
        if (borders.Remove(hwnd.ToInt64(), out DecorationForm? border)) border.Dispose();
        if (!stopping) { SaveJournal(); RetryPendingRecovery(); }
    }

    private void SaveJournal() => Recovery.Save(managed.Values.Select(window => window.Original).Concat(pendingRecovery.Values));

    private void RetryPendingRecovery()
    {
        foreach ((nint hwnd, WindowRestoreData original) in pendingRecovery.ToArray())
        {
            if (pendingRestores.Contains(hwnd)) continue;
            pendingRestores.Add(hwnd);
            _ = original.BeginRestore().ContinueWith(task => Post(() =>
            {
                pendingRestores.Remove(hwnd);
                if (task.IsCompletedSuccessfully && task.Result && pendingRecovery.TryGetValue(hwnd, out WindowRestoreData? current) && current == original)
                    pendingRecovery.Remove(hwnd);
                SaveJournal();
            }), TaskScheduler.Default);
        }
    }

    private void Reconcile()
    {
        if (stopping || paused || desktop.IsOverlayOpen) return;
        foreach ((nint hwnd, ManagedWindow record) in managed.ToArray())
        {
            if (record.Original.IsCurrentWindow() == false) ReleaseWindow(hwnd, false);
            else if (Native.IsIconic(hwnd) && !record.Hidden) ReleaseWindow(hwnd, true);
            else if (record.Deferred && !Native.IsHungAppWindow(hwnd)) ScheduleLayout();
        }
        RetryPendingRecovery();
        EnumerateExisting();
        nint foreground = Native.GetForegroundWindow();
        // A hidden window can stay the reported foreground window after its workspace was left.
        // Following it here would silently undo a workspace switch on the next maintenance tick.
        if (Environment.TickCount64 >= foregroundSyncBlockedUntil &&
            managed.TryGetValue(foreground, out ManagedWindow? foregroundWindow) && !foregroundWindow.Hidden &&
            state.FocusedWindow?.WindowId != foreground.ToInt64())
        { state.FocusWindow(foreground.ToInt64()); ScheduleLayout(); }
        foreach (WorkspaceBar bar in bars.Values) bar.Invalidate();
    }

    private void PointerFocus()
    {
        if (!Config.Settings.FocusFollowsMouse || SuspendBindings || !Native.GetCursorPos(out Native.Point pointer)) return;
        if (pointer.X == previousPointer.X && pointer.Y == previousPointer.Y) return;
        previousPointer = pointer;
        if (Native.GetAsyncKeyState(0x5B) < 0 || Native.GetAsyncKeyState(0x5C) < 0) return;
        nint hwnd = Native.GetAncestor(Native.WindowFromPoint(pointer), Native.GaRoot);
        if (managed.TryGetValue(hwnd, out ManagedWindow? record) && !record.Hidden && state.FocusedWindow?.WindowId != hwnd.ToInt64())
        {
            state.FocusWindow(hwnd.ToInt64());
            ScheduleLayout(true);
        }
    }

    public bool TryGetFloatingWindow(nint hwnd, out Rectangle bounds)
    {
        bounds = Rectangle.Empty;
        if (SuspendBindings || !managed.ContainsKey(hwnd) || !state.Windows.TryGetValue(hwnd.ToInt64(), out WindowNode? node) || !node.IsFloating) return false;
        state.FocusWindow(node.WindowId);
        bounds = ToRectangle(state.GetFloatingBounds(node.WindowId));
        wantFocus = true;
        return true;
    }

    public void UpdateFloatingBounds(nint hwnd, Rectangle bounds)
    {
        state.SetFloatingBounds(hwnd.ToInt64(), ToPixelRect(bounds));
        ScheduleLayout(true);
    }

    private void ScheduleLayout(bool focus = false)
    {
        if (stopping || initializing) return;
        wantFocus |= focus;
        if (focus) { focusIntentTime = unchecked((uint)Environment.TickCount); hasFocusIntent = true; }
        if (!layoutTimer.Enabled) layoutTimer.Start();
    }

    private void ApplyLayout(bool focus)
    {
        if (stopping || paused || initializing || applying || desktop.IsOverlayOpen) return;
        applying = true;
        try
        {
            foreach (WorkspaceBar bar in bars.Values)
            {
                OutputState? output = state.Outputs.FirstOrDefault(item => item.Name == bar.OutputName);
                bar.SetFullscreen(output is not null && state.GetWorkspace(output.ActiveWorkspace).FullscreenNode is not null);
            }
            LayoutSnapshot snapshot = state.CalculateLayout();
            nint foreground = Native.GetForegroundWindow();
            bool showDecorations = managed.ContainsKey(foreground) || Native.GetProcessId(foreground) == Environment.ProcessId;
            HashSet<long> visibleBorders = [];
            bool focusPending = false;
            foreach (WindowLayout layout in snapshot.Windows)
            {
                nint hwnd = (nint)layout.WindowId;
                if (!managed.TryGetValue(hwnd, out ManagedWindow? record) || !Native.IsWindow(hwnd)) continue;
                if (Native.DwmGetInt(hwnd, Native.DwmwaCloaked, 0) != 0) continue;
                record.IgnoreEventsUntil = Environment.TickCount64 + 250;
                record.Hidden = !layout.Visible;
                string borderType = layout.Floating ? Config.Settings.DefaultFloatingBorder : Config.Settings.DefaultBorder;
                bool stripTitlebar = layout.Fullscreen || (!options.KeepTitlebars && borderType is "pixel" or "none");
                bool selectedWindow = state.FocusedWindow?.WindowId == layout.WindowId;
                bool active = selectedWindow && (focus || foreground == hwnd);
                PixelRect content = layout.BorderWidth > 0
                    ? layout.Bounds.Inset(layout.BorderWidth, layout.BorderWidth, layout.BorderWidth, layout.BorderWidth) : layout.Bounds;
                Rectangle target = ToRectangle(content);
                if (record.TargetBounds != target) { record.TargetBounds = target; record.GeometryRetries = 0; }
                long version = Interlocked.Increment(ref record.RequestVersion);
                bool warp = Config.Settings.MouseWarping;
                record.Worker.Submit(() =>
                {
                    if (stopping || paused || desktop.IsOverlayOpen || Volatile.Read(ref record.RequestVersion) != version) return;
                    if (!record.Original.BeforeRestore.IsCompletedSuccessfully || !record.Original.RestorationCompletion.IsCompleted)
                    { Post(() => { record.Deferred = true; }); return; }
                    try
                    {
                        bool? identity = record.Original.IsCurrentWindow();
                        if (identity == false)
                        {
                            Post(() =>
                            {
                                if (!managed.TryGetValue(hwnd, out ManagedWindow? current) || current != record || record.RequestVersion != version) return;
                                ReleaseWindow(hwnd, false); ScheduleLayout();
                            });
                            return;
                        }
                        if (identity is null || !Native.IsResponsiveWindow(hwnd))
                        {
                            Post(() => { record.Deferred = true; });
                            return;
                        }
                        if (stopping || paused || desktop.IsOverlayOpen || Volatile.Read(ref record.RequestVersion) != version) return;
                        if (!layout.Visible) { SetManagedVisibility(hwnd, record, Native.SwHide); return; }
                        Theme.ConfigureFrame(hwnd, record.Original.Style, stripTitlebar);
                        if (stopping || paused || desktop.IsOverlayOpen || Volatile.Read(ref record.RequestVersion) != version) return;
                        Theme.Apply(hwnd, active, layout.BorderWidth > 0 && !stripTitlebar);
                        if (!Native.IsWindowVisible(hwnd) && Volatile.Read(ref record.RequestVersion) == version)
                            SetManagedVisibility(hwnd, record, Native.SwShownoactivate);
                        if (Volatile.Read(ref record.RequestVersion) != version) return;
                        if (Native.GetBounds(hwnd, true) != target && !Native.SetVisibleBounds(hwnd, target))
                            throw new Win32Exception("Windows denied changing this window's geometry.");
                        Rectangle actual = Native.GetBounds(hwnd, true);
                        if (focus && selectedWindow && !stopping && !paused && !desktop.IsOverlayOpen && Volatile.Read(ref record.RequestVersion) == version)
                        {
                            Native.FocusWindow(hwnd);
                            if (warp) Native.SetCursorPos(layout.Bounds.CenterX, layout.Bounds.CenterY);
                        }
                        Post(() =>
                        {
                            if (!managed.TryGetValue(hwnd, out ManagedWindow? current) || current != record || record.RequestVersion != version) return;
                            record.Deferred = false;
                            record.NativeBounds = actual;
                            record.IgnoreEventsUntil = Environment.TickCount64 + 250;
                            if (actual != target && record.GeometryRetries++ == 0) ScheduleLayout();
                            else if (actual.Size != target.Size && !record.Limited)
                            { record.Limited = true; AppLog.Write($"{layout.WindowId}: application minimum size prevents an exact tile."); }
                        });
                    }
                    catch (Win32Exception exception)
                    {
                        Post(() =>
                        {
                            if (!managed.TryGetValue(hwnd, out ManagedWindow? current) || current != record) return;
                            rejected[hwnd] = record.Original;
                            ReleaseWindow(hwnd, true, restorePlacement: true);
                            Notify($"Window skipped: {exception.Message} Elevated/protected windows may refuse tiling.");
                            ScheduleLayout();
                        });
                    }
                });
                if (layout.Visible && layout.BorderWidth > 0)
                {
                    visibleBorders.Add(layout.WindowId);
                    if (!borders.TryGetValue(layout.WindowId, out DecorationForm? border)) borders.Add(layout.WindowId, border = new(true));
                    border.UpdateBorder(ToRectangle(layout.Bounds), layout.BorderWidth, active);
                    border.Visible = showDecorations;
                }
                if (focus && selectedWindow && layout.Visible) focusPending = true;
            }
            if (focus)
            {
                // Focus changes triggered by hiding the old workspace must not rewrite the selection.
                foregroundSyncBlockedUntil = Math.Max(foregroundSyncBlockedUntil, Environment.TickCount64 + 500);
                if (focusPending && state.FocusedWindow is { } focusedWindow) BeginFocusWatch((nint)focusedWindow.WindowId);
            }
            foreach ((long id, DecorationForm border) in borders) if (!visibleBorders.Contains(id)) border.Hide();
            HashSet<long> visibleHeaders = [];
            foreach (ContainerDecoration decoration in snapshot.Decorations)
            {
                visibleHeaders.Add(decoration.NodeId);
                if (!headers.TryGetValue(decoration.NodeId, out DecorationForm? header))
                    headers.Add(decoration.NodeId, header = new(false, id => { state.FocusNodeById(id); ScheduleLayout(true); }));
                header.UpdateTabs(decoration);
                header.Visible = showDecorations;
            }
            foreach ((long id, DecorationForm header) in headers.ToArray())
                if (!visibleHeaders.Contains(id))
                {
                    if (state.FindNode(id) is null) { header.Dispose(); headers.Remove(id); }
                    else header.Hide();
                }
            foreach (WorkspaceBar bar in bars.Values) bar.Invalidate();
        }
        finally { applying = false; }
    }

    private void BeginFocusWatch(nint hwnd)
    {
        if (hwnd == 0 || stopping || paused) return;
        focusTarget = hwnd;
        focusDeadline = Environment.TickCount64 + 600;
        if (!focusTimer.Enabled) focusTimer.Start();
    }

    private void RetryFocus()
    {
        focusTimer.Stop();
        nint target = focusTarget;
        if (target == 0 || stopping || paused || desktop.IsOverlayOpen) return;
        if (!managed.TryGetValue(target, out ManagedWindow? record) || record.Hidden || !Native.IsWindowVisible(target))
        { focusTarget = 0; return; }
        nint current = Native.GetForegroundWindow();
        if (current == target) { focusTarget = 0; return; }
        // Retry only while an empty desktop, the shell surface or the stale hidden window owns focus.
        // Any real application the user switched to keeps its focus.
        bool stale = current == 0 || managed.TryGetValue(current, out ManagedWindow? currentRecord) && currentRecord.Hidden;
        if (!stale && !ShellClasses.Contains(Native.GetClass(current))) { focusTarget = 0; return; }
        Native.FocusWindow(target);
        if (Native.GetForegroundWindow() == target || Environment.TickCount64 >= focusDeadline)
        { focusTarget = 0; return; }
        foregroundSyncBlockedUntil = Math.Max(foregroundSyncBlockedUntil, Environment.TickCount64 + 400);
        focusTimer.Start();
    }

    private void RestoreFocus()
    {
        if (stopping || paused || desktop.IsOverlayOpen) return;
        ScheduleLayout(true);
    }

    private static void SetManagedVisibility(nint hwnd, ManagedWindow record, int command)
    {
        Interlocked.Exchange(ref record.VisibilityStart, Environment.TickCount64);
        Interlocked.Increment(ref record.VisibilityInFlight);
        try { Native.ShowWindow(hwnd, command); }
        finally
        {
            Interlocked.Exchange(ref record.VisibilityEnd, Environment.TickCount64);
            Interlocked.Decrement(ref record.VisibilityInFlight);
        }
    }

    private IEnumerable<OutputState> ReadOutputs()
    {
        foreach (Screen screen in Screen.AllScreens)
        {
            Rectangle workArea = screen.WorkingArea;
            if (bars.TryGetValue(screen.DeviceName, out WorkspaceBar? bar)) workArea = bar.ConstrainWorkArea(workArea);
            yield return new(screen.DeviceName, ToPixelRect(screen.Bounds), ToPixelRect(workArea), screen.Primary);
        }
    }

    private void CreateBars()
    {
        if (options.NoBar || !Config.Bar.Enabled) return;
        foreach (Screen screen in Screen.AllScreens)
        {
            if (bars.ContainsKey(screen.DeviceName)) continue;
            WorkspaceBar bar = new(screen.DeviceName, Config.Bar, () => state, () => paused ? "paused" : Mode,
                (output, name) => { state.FocusOutput(output); state.SwitchWorkspace(name); ScheduleLayout(true); },
                () => { displayUpdatePending = true; ScheduleLayout(); });
            bars.Add(screen.DeviceName, bar);
            bar.DockBar();
        }
    }

    private void UpdateDisplays()
    {
        foreach (string removed in bars.Keys.Where(name => Screen.AllScreens.All(screen => screen.DeviceName != name)).ToArray())
        { bars[removed].Dispose(); bars.Remove(removed); }
        if (options.NoBar || !Config.Bar.Enabled)
        {
            foreach (WorkspaceBar bar in bars.Values) bar.Dispose();
            bars.Clear();
        }
        CreateBars();
        state.SetOutputs(ReadOutputs());
    }

    private void DisplayChanged(object? sender, EventArgs e) => Post(() => { displayUpdatePending = true; ScheduleLayout(); });
    private void PreferencesChanged(object? sender, UserPreferenceChangedEventArgs e) => DisplayChanged(sender, e);

    public void ExecuteCommand(string command) => ExecuteSequence(command, true);

    private List<CommandResult> ExecuteSequence(string inputCommand, bool apply)
    {
        List<CommandResult> results = [];
        bool requestFocus = false;
        foreach (string raw in SwaySyntax.SplitCommands(Config.Expand(inputCommand)))
        {
            try
            {
                (WindowCriteria? criteria, string command) = SwaySyntax.ExtractCriteria(raw);
                string action = SwaySyntax.Tokenize(command).FirstOrDefault()?.ToLowerInvariant() ?? "";
                bool focusAction = action is "focus" or "workspace" or "floating" or "fullscreen" or "move" or "scratchpad";
                if (criteria is null) ExecuteSingle(command);
                else
                {
                    if (!criteria.IsValid) throw new InvalidOperationException(string.Join("; ", criteria.ValidationErrors));
                    WindowIdentity? focused = state.FocusedWindow is { } focusWindow ? Identity(focusWindow) : null;
                    WindowNode[] targets = state.Windows.Values.Where(node => criteria.Matches(Identity(node), focused)).ToArray();
                    if (targets.Length == 0) throw new InvalidOperationException("No window matched the criteria.");
                    long? previous = state.FocusedWindow?.WindowId;
                    foreach (WindowNode target in targets)
                    {
                        if (command.Trim() == "scratchpad show") state.ShowScratchpad(target.WindowId);
                        else if (state.FocusWindow(target.WindowId)) ExecuteSingle(command);
                    }
                    if (action is not ("focus" or "scratchpad") && previous is long id && state.Windows.ContainsKey(id)) state.FocusWindow(id);
                }
                requestFocus |= focusAction;
                results.Add(new(true));
            }
            catch (Exception exception) when (exception is InvalidOperationException or ArgumentException or Win32Exception or IOException or UnauthorizedAccessException)
            { results.Add(new(false, exception.Message)); Notify(exception.Message); }
        }
        if (apply) ScheduleLayout(requestFocus);
        return results;
    }

    private void ExecuteSingle(string command)
    {
        List<string> words = SwaySyntax.Tokenize(command);
        if (words.Count == 0) return;
        string action = words[0].ToLowerInvariant();
        string argument = words.Count > 1 ? words[1].ToLowerInvariant() : "";
        switch (action)
        {
            case "exec":
            case "exec_always":
                string tail = command[(command.IndexOf(words[0], StringComparison.Ordinal) + words[0].Length)..].TrimStart();
                if (tail.StartsWith("--no-startup-id", StringComparison.Ordinal)) tail = tail[15..].TrimStart();
                if (!desktop.TryExecute(tail) && !desktop.TryLaunchWindows(tail))
                    throw new InvalidOperationException($"No native Windows translation for: {tail}");
                break;
            case "kill":
                foreach (WindowNode window in (state.SelectedNode?.DescendantWindows() ?? []).ToArray())
                    Native.PostMessage((nint)window.WindowId, Native.WmClose, 0, 0);
                break;
            case "reload": Reload(); break;
            case "exit":
                if (handlingIpc) exitAfterReply = true;
                else Post(Stop);
                break;
            case "mode":
                string name = SwaySyntax.Unquote(command[4..]);
                if (name != "default" && !Config.Bindings.Any(binding => binding.Mode == name)) throw new InvalidOperationException($"Unknown binding mode '{name}'.");
                Mode = name;
                break;
            case "splith": state.Split(false); break;
            case "splitv": state.Split(true); break;
            case "split": state.Split(argument is "v" or "vertical"); break;
            case "layout": state.SetLayout(string.Join(' ', words.Skip(1))); break;
            case "floating": state.ToggleFloating(argument.Length == 0 ? "toggle" : argument); break;
            case "fullscreen": state.ToggleFullscreen(argument.Length == 0 ? "toggle" : argument); break;
            case "focus":
                if (argument.Length == 0) break;
                if (TryDirection(argument, out Direction direction)) state.FocusDirection(direction);
                else if (argument == "parent") state.FocusParent();
                else if (argument == "child") state.FocusChild();
                else if (argument is "floating" or "tiling" or "mode_toggle") state.FocusMode(argument);
                else if (argument is "next" or "prev") state.FocusSibling(argument == "next");
                else if (argument == "output" && words.Count > 2) FocusOutput(words[2]);
                else throw new InvalidOperationException($"Unsupported focus command: {command}");
                break;
            case "workspace":
                if (argument == "back_and_forth") state.BackAndForth();
                else if (argument is "next" or "prev") state.NextWorkspace(argument == "next");
                else
                {
                    bool noBack = words.Contains("--no-auto-back-and-forth");
                    List<string> targetWords = words.Skip(1).Where(word => word != "--no-auto-back-and-forth").ToList();
                    string workspace = WorkspaceName(targetWords);
                    string? output = Config.WorkspaceOutputs.TryGetValue(workspace, out string? alias) ? ResolveOutput(alias) : null;
                    state.SwitchWorkspace(workspace, noBack, output);
                }
                break;
            case "move": Move(words); break;
            case "scratchpad":
                if (argument != "show") throw new InvalidOperationException($"Unsupported scratchpad command: {command}");
                state.ShowScratchpad();
                break;
            case "resize": Resize(words); break;
            case "gaps": ChangeGaps(words); break;
            case "rename":
                int to = words.IndexOf("to");
                if (argument != "workspace" || to < 0 || to + 1 == words.Count) throw new InvalidOperationException("Use rename workspace to <name>.");
                state.RenameWorkspace(string.Join(' ', words.Skip(to + 1)));
                break;
            case "nop": break;
            default: throw new InvalidOperationException($"Sway command is not implemented on Windows: {action}");
        }
    }

    private void Move(List<string> words)
    {
        if (words.Count < 2) throw new InvalidOperationException("Move requires a target.");
        if (TryDirection(words[1], out Direction direction))
        { state.MoveDirection(direction, words.Count > 2 ? ParseAmount(words[2]) : 10); return; }
        if (words.Contains("scratchpad")) { state.MoveToScratchpad(); return; }
        int workspaceAt = words.IndexOf("workspace");
        int outputAt = words.IndexOf("output");
        if (workspaceAt >= 0 && outputAt > workspaceAt && outputAt + 1 < words.Count)
        { state.MoveWorkspaceToOutput(ResolveOutput(words[outputAt + 1])); return; }
        if (workspaceAt >= 0)
        { state.MoveFocusedToWorkspace(WorkspaceName(words.Skip(workspaceAt + 1).ToList())); return; }
        if (outputAt >= 0 && outputAt + 1 < words.Count)
        {
            string output = ResolveOutput(words[outputAt + 1]);
            OutputState target = state.Outputs.First(item => item.Name == output);
            state.MoveFocusedToWorkspace(target.ActiveWorkspace);
            return;
        }
        int positionAt = words.IndexOf("position");
        if (positionAt >= 0 && state.FocusedWindow is { IsFloating: true } window)
        {
            PixelRect rectangle = state.GetFloatingBounds(window.WindowId);
            if (words.Count > positionAt + 1 && words[positionAt + 1] == "center")
                rectangle = rectangle with { X = state.ActiveOutput.WorkArea.CenterX - rectangle.Width / 2, Y = state.ActiveOutput.WorkArea.CenterY - rectangle.Height / 2 };
            else if (words.Count > positionAt + 2)
                rectangle = rectangle with { X = ParseAmount(words[positionAt + 1]), Y = ParseAmount(words[positionAt + 2]) };
            else throw new InvalidOperationException("Use move position center or move position <x> <y>.");
            state.SetFloatingBounds(window.WindowId, rectangle);
            return;
        }
        throw new InvalidOperationException("Unsupported move command.");
    }

    private void Resize(List<string> words)
    {
        if (words.Count < 4) throw new InvalidOperationException("Use resize grow/shrink width/height <amount>px.");
        if (words[1] == "set")
        {
            if (state.FocusedWindow is not { IsFloating: true } window) throw new InvalidOperationException("Resize set is currently supported for floating windows.");
            PixelRect bounds = state.GetFloatingBounds(window.WindowId);
            if (words[2] is "width" or "height")
                bounds = words[2] == "width" ? bounds with { Width = ParseAmount(words[3]) } : bounds with { Height = ParseAmount(words[3]) };
            else bounds = bounds with { Width = ParseAmount(words[2]), Height = ParseAmount(words[3]) };
            state.SetFloatingBounds(window.WindowId, bounds);
            return;
        }
        if (words[1] is not ("grow" or "shrink") || words[2] is not ("width" or "height")) throw new InvalidOperationException("Unsupported resize operation.");
        int sign = words[1] == "grow" ? 1 : -1;
        state.Resize(words[2] == "width" ? ResizeAxis.Width : ResizeAxis.Height, sign * ParseAmount(words[3]), words[3].EndsWith("ppt", StringComparison.Ordinal));
    }

    private void ChangeGaps(List<string> words)
    {
        if (words.Count < 3) throw new InvalidOperationException("Use gaps inner/outer current set/inc/dec <pixels>.");
        int amount = ParseAmount(words[^1]);
        string operation = words.Count > 3 ? words[^2] : "set";
        int old = words[1] == "inner" ? state.Settings.GapInner : state.Settings.GapOuter;
        int value = Math.Max(0, operation == "plus" || operation == "inc" ? old + amount : operation == "minus" || operation == "dec" ? old - amount : amount);
        if (words[1] == "inner") state.Settings.GapInner = value;
        else if (words[1] == "outer") state.Settings.GapOuter = value;
        else throw new InvalidOperationException("Runtime gaps currently supports inner and outer.");
    }

    private string WorkspaceName(List<string> words)
    {
        if (words.Count == 0) throw new InvalidOperationException("Workspace name is empty.");
        if (words[0] == "number")
        {
            if (words.Count < 2) throw new InvalidOperationException("Workspace number is empty.");
            string number = words[1];
            return state.Workspaces.Keys.FirstOrDefault(name => name.Split(':')[0] == number) ?? string.Join(' ', words.Skip(1));
        }
        return string.Join(' ', words);
    }

    private string ResolveOutput(string alias)
    {
        if (Config.OutputMappings.TryGetValue(alias, out string? mapped)) alias = mapped;
        if (alias == "primary") return state.Outputs.FirstOrDefault(output => output.Primary)?.Name ?? state.Outputs[0].Name;
        OutputState? match = state.Outputs.FirstOrDefault(output => output.Name.Equals(alias, StringComparison.OrdinalIgnoreCase));
        if (match is null) throw new InvalidOperationException($"Unknown Windows output '{alias}'. Map it with windows_output in windows.conf.");
        return match.Name;
    }

    private void FocusOutput(string alias)
    {
        if (TryDirection(alias, out Direction direction)) state.FocusOutput(direction);
        else state.FocusOutput(ResolveOutput(alias));
    }

    private static bool TryDirection(string value, out Direction direction) => Enum.TryParse(value, true, out direction) && Enum.IsDefined(direction);
    private static int ParseAmount(string value)
    {
        Match match = Regex.Match(value, "^-?[0-9]+", RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        if (!match.Success || !int.TryParse(match.Value, CultureInfo.InvariantCulture, out int number)) throw new InvalidOperationException($"Invalid size '{value}'.");
        return number;
    }

    private void Reload()
    {
        SwayConfig updated = new SwayConfigLoader().Load(options.ConfigPath, options.OverridesPath);
        if (updated.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error))
            throw new InvalidOperationException("Reload rejected: " + string.Join("; ", updated.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).Select(item => item.Message)));
        Config = updated;
        state.Settings = updated.Settings;
        foreach (ManagedWindow window in managed.Values) window.AppliedRules.Clear();
        if (Mode != "default" && !updated.Bindings.Any(binding => binding.Mode == Mode)) Mode = "default";
        input.RefreshBindings();
        foreach (WorkspaceBar bar in bars.Values) bar.Configure(updated.Bar);
        displayUpdatePending = true;
        foreach (string command in options.NoAutostart ? [] : updated.ReloadCommands) ExecuteSequence("exec " + updated.Expand(command), false);
        AppLog.Write("Configuration reloaded.");
    }

    private Task<string> HandleRequest(string request)
    {
        TaskCompletionSource<string> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        Post(() =>
        {
            handlingIpc = true;
            try
            {
                string result = request.Trim() switch
                {
                    "get_tree" => JsonSerializer.Serialize(new { id = 0, type = "root", name = "SwayWin", nodes = state.Outputs.Select(OutputTree).ToArray() }),
                    "get_workspaces" => JsonSerializer.Serialize(state.Workspaces.Values.Select(workspace => new
                    {
                        name = workspace.Name, num = int.TryParse(workspace.Name.Split(':')[0], out int number) ? number : -1,
                        output = workspace.OutputName, focused = workspace == state.CurrentWorkspace,
                        visible = state.Outputs.Any(output => output.ActiveWorkspace == workspace.Name),
                        urgent = workspace.AllWindows().Any(window => window.Urgent)
                    })),
                    "get_outputs" => JsonSerializer.Serialize(state.Outputs.Select(output => new
                    { name = output.Name, active = true, primary = output.Primary, current_workspace = output.ActiveWorkspace, rect = output.Bounds, workarea = output.WorkArea })),
                    _ => JsonSerializer.Serialize(ExecuteSequence(request.StartsWith("command ", StringComparison.Ordinal) ? request[8..] : request, true))
                };
                completion.TrySetResult(result);
            }
            catch (Exception exception) { completion.TrySetResult(JsonSerializer.Serialize(new[] { new CommandResult(false, exception.Message) })); }
            finally { handlingIpc = false; }
        });
        return completion.Task;
    }

    private object OutputTree(OutputState output) => new
    {
        name = output.Name, type = "output", rect = output.Bounds,
        nodes = state.Workspaces.Values.Where(workspace => workspace.OutputName == output.Name).Select(workspace => new
        {
            id = workspace.Root.Id, name = workspace.Name, type = "workspace", layout = workspace.Root.Layout.ToString(),
            nodes = workspace.Root.Children.Select(NodeTree).ToArray(), floating_nodes = workspace.Floating.Select(NodeTree).ToArray()
        }).ToArray()
    };

    private object NodeTree(LayoutNode node) => new
    {
        id = node.Id, name = node is WindowNode window ? window.Title : "container", type = "con", rect = node.Bounds,
        window = node is WindowNode leaf ? (long?)leaf.WindowId : null,
        app_id = node is WindowNode app ? managed[(nint)app.WindowId].ProcessName : null,
        layout = node is ContainerNode container ? container.Layout.ToString() : "none",
        focused = node == state.SelectedNode, floating = node.IsFloating, scratchpad = node.IsScratchpad,
        nodes = node is ContainerNode parent ? parent.Children.Select(NodeTree).ToArray() : []
    };

    private void TogglePause()
    {
        paused = !paused;
        pauseItem.Text = paused ? "Resume tiling" : "Pause tiling";
        if (paused)
        {
            foreach (DecorationForm form in borders.Values.Concat(headers.Values)) form.Hide();
            foreach (ManagedWindow window in managed.Values)
            {
                Interlocked.Increment(ref window.RequestVersion);
                window.Original.BeforeRestore = Task.WhenAll(window.Original.BeforeRestore, window.Worker.Cancel());
                window.Hidden = false;
            }
            pauseItem.Enabled = false;
            WindowRestoreData[] originals = managed.Values.Select(window => window.Original).ToArray();
            _ = Task.Run(() => Recovery.RestoreWindows(originals)).ContinueWith(task => Post(() =>
            {
                pauseItem.Enabled = true;
                if (!task.IsCompletedSuccessfully || task.Result.Remaining.Count > 0) Notify("Some windows are unresponsive; their recovery records are retained.");
            }), TaskScheduler.Default);
            Mode = "default";
        }
        else
        {
            foreach (ManagedWindow window in managed.Values) window.Worker = new();
            Reconcile(); ScheduleLayout(true);
        }
        foreach (WorkspaceBar bar in bars.Values) bar.Invalidate();
    }

    private void OpenOverrides()
    {
        string? path = options.OverridesPath;
        if (path is not null) Process.Start(new ProcessStartInfo("notepad.exe", $"\"{path}\"") { UseShellExecute = true });
    }

    private void Notify(string message)
    {
        if (dispatcher.InvokeRequired) { Post(() => Notify(message)); return; }
        AppLog.Write(message);
        if (!stopping && tray.Visible) tray.ShowBalloonTip(4000, "SwayWin", message.Length > 240 ? message[..240] : message, ToolTipIcon.Info);
    }

    internal void Stop()
    {
        if (stopping) return;
        stopping = true;
        maintenance.Stop(); hover.Stop(); layoutTimer.Stop(); focusTimer.Stop(); focusTarget = 0;
        input.Dispose();
        foreach (nint hook in eventHooks) Native.UnhookWinEvent(hook);
        eventHooks.Clear();
        commands?.Dispose();
        foreach (WorkspaceBar bar in bars.Values) bar.Dispose();
        bars.Clear();
        foreach (DecorationForm form in borders.Values.Concat(headers.Values)) form.Dispose();
        borders.Clear(); headers.Clear();
        foreach (ManagedWindow window in managed.Values)
        {
            Interlocked.Increment(ref window.RequestVersion);
            window.Original.BeforeRestore = Task.WhenAll(window.Original.BeforeRestore, window.Worker.Cancel());
        }
        try
        {
            (List<WindowRestoreData> remaining, _) = Recovery.RestoreWindows(managed.Values.Select(window => window.Original).Concat(pendingRecovery.Values));
            if (remaining.Count == 0) Recovery.Clear();
            else
            {
                Recovery.Save(remaining);
                AppLog.Write($"{remaining.Count} window recovery records retained for watchdog/--recover.");
            }
        }
        catch (Exception exception) { AppLog.Write($"Exit recovery: {exception}"); }
        finally
        {
            tray.Visible = false;
            AppLog.Write("Exited; outstanding recovery data is retained if needed.");
            ExitThread();
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
            SystemEvents.DisplaySettingsChanged -= DisplayChanged;
            SystemEvents.UserPreferenceChanged -= PreferencesChanged;
            focusTimer.Stop(); focusTimer.Dispose();
            maintenance.Dispose(); hover.Dispose(); layoutTimer.Dispose();
            desktop.Dispose(); tray.ContextMenuStrip?.Dispose(); tray.Dispose(); dispatcher.Dispose();
        }
        base.Dispose(disposing);
    }

    internal static Rectangle ToRectangle(PixelRect rectangle) => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
    internal static PixelRect ToPixelRect(Rectangle rectangle) => new(rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height);
}

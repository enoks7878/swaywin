using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using SwayWin.Core.Configuration;

namespace SwayWin;

internal interface IInputHost
{
    SwayConfig Config { get; }
    string Mode { get; }
    bool SuspendBindings { get; }
    void Post(Action action);
    void ExecuteCommand(string command);
    void RequestEmergencyExit();
    bool TryGetFloatingWindow(nint hwnd, out Rectangle bounds);
    void UpdateFloatingBounds(nint hwnd, Rectangle bounds);
}

internal sealed class InputHooks : IDisposable
{
    // Retain even a disposed owner if Windows refuses to remove one of its hooks.
    private static readonly HashSet<InputHooks> HookOwners = [];
    private static readonly int[] LetterScans =
    [
        0x1E, 0x30, 0x2E, 0x20, 0x12, 0x21, 0x22, 0x23, 0x17,
        0x24, 0x25, 0x26, 0x32, 0x31, 0x18, 0x19, 0x10, 0x13,
        0x1F, 0x14, 0x16, 0x2F, 0x11, 0x2D, 0x15, 0x2C
    ];

    private const int MaxFloatingSize = 32767;
    private const int InvalidHookHandle = 1404;
    private const Modifiers EmergencyModifiers = Modifiers.Control | Modifiers.Alt | Modifiers.Shift;

    private readonly IInputHost _host;
    private readonly Native.HookProc _keyboardCallback;
    private readonly Native.HookProc _mouseCallback;
    private readonly Action _applyDragUpdate;
    private readonly bool[] _heldKeys = new bool[768];
    private readonly bool[] _lockKeys = new bool[768];
    private readonly KeyCapture?[] _capturedKeys = new KeyCapture?[768];
    private readonly byte[] _unicodeState = new byte[256];
    private readonly StringBuilder _unicodeBuffer = new(8);
    private readonly List<string> _runtimeWarnings = [];

    private BindingSet _bindings;
    private nint _keyboardHook;
    private nint _mouseHook;
    private int _ownerThread;
    private int _generation;
    private volatile bool _disposed;
    private volatile bool _started;
    private bool _keyboardFaultReported;
    private bool _mouseFaultReported;
    private bool _menuFaultReported;
    private bool _emergencyQueued;
    private Modifiers _menuMaskModifiers;
    private bool _menuMaskAttempted;
    private bool _menuMasked;
    private int _suppressedMouseButtons;
    private DragSession? _drag;
    private DragUpdate? _pendingDragUpdate;
    private bool _dragUpdatePosted;

    public InputHooks(IInputHost host)
    {
        ArgumentNullException.ThrowIfNull(host);
        _host = host;
        _keyboardCallback = KeyboardCallback;
        _mouseCallback = MouseCallback;
        _applyDragUpdate = ApplyDragUpdate;
        _bindings = CompileBindings(host.Config);
        Warnings = _bindings.Warnings;
    }

    public IReadOnlyList<string> Warnings { get; private set; }

    public static IReadOnlyList<string> ValidateBindings(SwayConfig config) => CompileBindings(config).Warnings;

    public void RefreshBindings()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        CheckOwnerThread();
        _bindings = CompileBindings(_host.Config);
        _generation++;
        CancelDrag();
        UpdateWarnings();
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!OperatingSystem.IsWindows())
            throw new PlatformNotSupportedException("Low-level input hooks require Windows.");
        if (!Application.MessageLoop || Thread.CurrentThread.GetApartmentState() != ApartmentState.STA)
            throw new InvalidOperationException("Start input hooks on the STA Windows Forms UI message-loop thread.");
        CheckOwnerThread();
        if (_keyboardHook != 0 && _mouseHook != 0)
            return;
        if (_keyboardHook != 0 || _mouseHook != 0)
            throw new InvalidOperationException("A previous input hook could not be removed; dispose it before retrying.");

        RefreshBindings();
        _ownerThread = Environment.CurrentManagedThreadId;
        nint module = Native.GetModuleHandle(null);
        if (module == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not obtain the module for input hooks.");

        _keyboardHook = Native.SetWindowsHookEx(13, _keyboardCallback, module, 0);
        if (_keyboardHook == 0)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the low-level keyboard hook.");
        lock (HookOwners)
            HookOwners.Add(this);

        try
        {
            _mouseHook = Native.SetWindowsHookEx(14, _mouseCallback, module, 0);
            if (_mouseHook == 0)
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not install the low-level mouse hook.");
            _started = true;
        }
        catch
        {
            RemoveHooks();
            throw;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        CancelDrag();
        RemoveHooks();
        GC.KeepAlive(_keyboardCallback);
        GC.KeepAlive(_mouseCallback);
    }

    private void CheckOwnerThread()
    {
        if (_ownerThread != 0 && Environment.CurrentManagedThreadId != _ownerThread)
            throw new InvalidOperationException("Refresh and start input hooks on their UI message-loop thread.");
    }

    private void RemoveHooks()
    {
        _started = false;
        try
        {
            RemoveHook(ref _keyboardHook, "keyboard");
        }
        finally
        {
            try
            {
                RemoveHook(ref _mouseHook, "mouse");
            }
            finally
            {
                if (_keyboardHook == 0 && _mouseHook == 0)
                {
                    lock (HookOwners)
                        HookOwners.Remove(this);
                }
            }
        }
    }

    private void RemoveHook(ref nint hook, string kind)
    {
        if (hook == 0)
            return;
        try
        {
            if (Native.UnhookWindowsHookEx(hook))
            {
                hook = 0;
                return;
            }
            int error = Marshal.GetLastWin32Error();
            if (error == InvalidHookHandle)
                hook = 0;
            else
                AddRuntimeWarning($"Could not remove the {kind} hook (Win32 error {error}); its callback is disabled.");
        }
        catch (Exception ex)
        {
            AddRuntimeWarning($"Could not remove the {kind} hook: {ex.Message}");
        }
    }

    private nint KeyboardCallback(int code, nint wParam, nint lParam)
    {
        if (code != 0 || _disposed || !_started)
            return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
        uint message = unchecked((uint)wParam);
        bool down = message is 0x0100 or 0x0104;
        if (!down && message is not (0x0101 or 0x0105))
            return Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);

        bool suppress = false;
        try
        {
            var data = Marshal.PtrToStructure<Native.KeyboardData>(lParam);
            if ((data.Flags & 0x12) == 0 && data.VirtualKey < 256)
                HandleKeyboard(data, down, ref suppress);
        }
        catch (Exception ex)
        {
            if (!_keyboardFaultReported)
            {
                _keyboardFaultReported = true;
                PostWarning($"Keyboard hook callback failed: {ex.Message}");
            }
        }
        return suppress ? 1 : Native.CallNextHookEx(_keyboardHook, code, wParam, lParam);
    }

    private void HandleKeyboard(Native.KeyboardData data, bool down, ref bool suppress)
    {
        int key = NormalizeModifier(data);
        int slot = KeySlot(data);
        KeyCapture? capture = _capturedKeys[slot];
        suppress = capture is not null;
        Modifiers modifiers = ReadModifiers(key, down);

        // The async state does not yet include the event currently in this hook.
        bool repeat = _heldKeys[slot] || (down && IsDown((int)data.VirtualKey));
        _heldKeys[slot] = down;
        if (!down && data.VirtualKey == 0x7B)
            _emergencyQueued = false;
        if (!down && (key is 0x5B or 0x5C or 0xA4 or 0xA5) && !_menuMasked && _menuMaskModifiers != 0)
            TryMaskModifierMenus(true);
        _menuMaskModifiers &= modifiers;
        if (_menuMaskModifiers == 0)
        {
            _menuMaskAttempted = false;
            _menuMasked = false;
        }

        if (_host.SuspendBindings || (modifiers & _bindings.FloatingModifiers) != _bindings.FloatingModifiers)
            CancelDrag();

        // Even a symbol binding on the Russian L key must leave the system lock intact.
        if (down && (modifiers & Modifiers.Windows) != 0 && data.VirtualKey == 0x4C)
        {
            CancelDrag();
            Array.Clear(_capturedKeys);
            Array.Clear(_heldKeys);
            _lockKeys[slot] = true;
            suppress = false;
            return;
        }
        if (_lockKeys[slot])
        {
            if (!down)
                _lockKeys[slot] = false;
            suppress = false;
            return;
        }

        if (down && data.VirtualKey == 0x7B && modifiers == EmergencyModifiers)
        {
            if (capture is not null || !repeat)
            {
                _capturedKeys[slot] = new KeyCapture(null, null, modifiers, _generation, true);
                suppress = true;
                MaskModifierMenus(modifiers);
            }
            if (!_emergencyQueued)
            {
                _emergencyQueued = true;
                try
                {
                    _host.Post(() =>
                    {
                        if (!_disposed)
                            _host.RequestEmergencyExit();
                    });
                }
                catch (Exception ex)
                {
                    _emergencyQueued = false;
                    PostWarning($"Could not queue emergency exit: {ex.Message}");
                }
            }
            return;
        }

        if (!down)
        {
            _capturedKeys[slot] = null;
            if (capture?.Release is { } release && IsCaptureActive(capture))
                PostCommand(release, capture.Generation);
            return;
        }

        if (capture is not null)
        {
            if (!capture.Emergency && IsCaptureActive(capture) && modifiers == capture.Modifiers &&
                capture.Press is { NoRepeat: false } press)
                PostCommand(press, capture.Generation);
            return;
        }

        // Never swallow the up of a key whose original down was delivered to another app.
        if (repeat)
            return;

        if (_host.SuspendBindings || !_bindings.Modes.TryGetValue(_host.Mode, out ModeBindings? mode))
            return;
        BindingPair match = FindBindings(mode, modifiers, data, slot);
        if (match.Press is null && match.Release is null)
            return;

        _capturedKeys[slot] = new KeyCapture(match.Press, match.Release, modifiers, _generation, false);
        suppress = true;
        MaskModifierMenus(modifiers);
        if (match.Press is { } binding)
            PostCommand(binding, _generation);
    }

    private bool IsCaptureActive(KeyCapture capture) =>
        !_host.SuspendBindings && capture.Generation == _generation &&
        string.Equals(_host.Mode, (capture.Press ?? capture.Release)?.Mode, StringComparison.Ordinal);

    private void PostCommand(CompiledBinding binding, int generation)
    {
        try
        {
            _host.Post(() =>
            {
                if (!_disposed && generation == _generation && !_host.SuspendBindings &&
                    string.Equals(_host.Mode, binding.Mode, StringComparison.Ordinal))
                    _host.ExecuteCommand(binding.Command);
            });
        }
        catch (Exception ex)
        {
            PostWarning($"Could not queue a binding command: {ex.Message}");
        }
    }

    private BindingPair FindBindings(ModeBindings mode, Modifiers modifiers, Native.KeyboardData data, int slot)
    {
        var match = new BindingPair();
        MergeMatch(ref match, mode.Scans, Chord(modifiers, slot));
        MergeMatch(ref match, mode.VirtualKeys, Chord(modifiers, (int)data.VirtualKey));
        if ((mode.SymbolModifiers & (1 << (int)modifiers)) == 0)
            return match;

        uint thread = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out _);
        nint layout = Native.GetKeyboardLayout(thread);
        // Clear Ctrl/Alt/Win and locks; digits and Shift+minus match their unshifted base symbol.
        char symbol = TranslateSymbol(data, layout, false);
        if (symbol != '\0')
            MergeMatch(ref match, mode.Symbols, Chord(modifiers, symbol));
        if ((modifiers & Modifiers.Shift) != 0)
        {
            char shifted = TranslateSymbol(data, layout, true);
            if (shifted != '\0' && shifted != symbol)
                MergeMatch(ref match, mode.Symbols, Chord(modifiers, shifted));
        }
        return match;
    }

    private char TranslateSymbol(Native.KeyboardData data, nint layout, bool shifted)
    {
        _unicodeState[0x10] = shifted ? (byte)0x80 : (byte)0;
        _unicodeBuffer.Clear();
        // Flag 4 prevents translation from changing the foreground application's dead-key state.
        int count = Native.ToUnicodeEx(data.VirtualKey, data.ScanCode, _unicodeState,
            _unicodeBuffer, _unicodeBuffer.Capacity, 4, layout);
        return count == 1 && _unicodeBuffer.Length == 1 ? char.ToLowerInvariant(_unicodeBuffer[0]) : '\0';
    }

    private static void MergeMatch(ref BindingPair result, Dictionary<int, BindingPair> index, int chord)
    {
        if (!index.TryGetValue(chord, out BindingPair candidate))
            return;
        if (candidate.Press is { } press && (result.Press is null || press.Order > result.Press.Order))
            result.Press = press;
        if (candidate.Release is { } release && (result.Release is null || release.Order > result.Release.Order))
            result.Release = release;
    }

    private nint MouseCallback(int code, nint wParam, nint lParam)
    {
        if (code != 0 || _disposed || !_started)
            return Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
        bool suppress = false;
        try
        {
            var data = Marshal.PtrToStructure<Native.MouseData>(lParam);
            if ((data.Flags & 0x03) == 0)
                HandleMouse(unchecked((uint)wParam), data.Position, ref suppress);
        }
        catch (Exception ex)
        {
            CancelDrag();
            if (!_mouseFaultReported)
            {
                _mouseFaultReported = true;
                PostWarning($"Mouse hook callback failed: {ex.Message}");
            }
        }
        return suppress ? 1 : Native.CallNextHookEx(_mouseHook, code, wParam, lParam);
    }

    private void HandleMouse(uint message, Native.Point point, ref bool suppress)
    {
        int button = message is 0x0201 or 0x0202 ? 1 : message is 0x0204 or 0x0205 ? 2 : 0;
        bool up = message is 0x0202 or 0x0205;
        if (up && (_suppressedMouseButtons & button) != 0)
        {
            suppress = true;
            _suppressedMouseButtons &= ~button;
            if (_drag is { } ending && ending.Button == button)
            {
                if (!_host.SuspendBindings && (ReadModifiers() & ending.Modifiers) == ending.Modifiers)
                    QueueDragUpdate(ending, point);
                else
                    ending.Cancelled = true;
                _drag = null;
            }
            return;
        }

        Modifiers modifiers = ReadModifiers();
        if (_host.SuspendBindings || (_drag is { } active && (modifiers & active.Modifiers) != active.Modifiers))
            CancelDrag();
        if (message == 0x0200)
        {
            if (_drag is { } moving)
                QueueDragUpdate(moving, point);
            // Do not swallow pointer motion: the real cursor must continue to follow the mouse.
            return;
        }
        if (message is not (0x0201 or 0x0204))
            return;

        // A fresh down also repairs state if a button-up happened on another desktop.
        _suppressedMouseButtons &= ~button;
        if (_drag is { } previous && previous.Button == button)
            CancelDrag();
        if (_drag is not null || _host.SuspendBindings || _bindings.FloatingModifiers == 0 ||
            modifiers != _bindings.FloatingModifiers)
            return;

        nint child = Native.WindowFromPoint(point);
        nint root = child == 0 ? 0 : Native.GetAncestor(child, 2);
        if (root == 0 || !_host.TryGetFloatingWindow(root, out Rectangle bounds) ||
            bounds.Width <= 0 || bounds.Height <= 0)
            return;

        _drag = new DragSession(root, bounds, point, button, modifiers);
        _suppressedMouseButtons |= button;
        suppress = true;
        MaskModifierMenus(modifiers);
    }

    private void QueueDragUpdate(DragSession drag, Native.Point point)
    {
        long dx = (long)point.X - drag.Origin.X;
        long dy = (long)point.Y - drag.Origin.Y;
        Rectangle initial = drag.InitialBounds;
        long x = initial.X;
        long y = initial.Y;
        int width = initial.Width;
        int height = initial.Height;
        if (drag.Button == 1)
        {
            x += dx;
            y += dy;
        }
        else
        {
            width = (int)Math.Clamp((long)initial.Width + (drag.ResizeLeft ? -dx : dx),
                _bindings.MinWidth, _bindings.MaxWidth);
            height = (int)Math.Clamp((long)initial.Height + (drag.ResizeTop ? -dy : dy),
                _bindings.MinHeight, _bindings.MaxHeight);
            if (drag.ResizeLeft)
                x += (long)initial.Width - width;
            if (drag.ResizeTop)
                y += (long)initial.Height - height;
        }
        var bounds = new Rectangle((int)Math.Clamp(x, int.MinValue, (long)int.MaxValue - width),
            (int)Math.Clamp(y, int.MinValue, (long)int.MaxValue - height), width, height);
        if (bounds == drag.LastBounds)
            return;
        drag.LastBounds = bounds;
        _pendingDragUpdate = new DragUpdate(drag, bounds);
        if (_dragUpdatePosted)
            return;
        _dragUpdatePosted = true;
        try
        {
            _host.Post(_applyDragUpdate);
        }
        catch
        {
            _dragUpdatePosted = false;
            _pendingDragUpdate = null;
            throw;
        }
    }

    private void ApplyDragUpdate()
    {
        DragUpdate? update = _pendingDragUpdate;
        _pendingDragUpdate = null;
        _dragUpdatePosted = false;
        if (!_disposed && update is { } pending && !pending.Drag.Cancelled && !_host.SuspendBindings &&
            _host.TryGetFloatingWindow(pending.Drag.Window, out _))
            _host.UpdateFloatingBounds(pending.Drag.Window, pending.Bounds);
    }

    private void CancelDrag()
    {
        if (_drag is { } drag)
            drag.Cancelled = true;
        _drag = null;
        _pendingDragUpdate = null;
        // Keep swallowing only the up corresponding to a down that was never delivered.
    }

    private void MaskModifierMenus(Modifiers modifiers)
    {
        Modifiers menus = modifiers & (Modifiers.Windows | Modifiers.Alt);
        if (menus == 0)
            return;
        if ((menus & ~_menuMaskModifiers) != 0)
        {
            _menuMaskAttempted = false;
            _menuMasked = false;
        }
        _menuMaskModifiers |= menus;
        TryMaskModifierMenus(false);
    }

    private void TryMaskModifierMenus(bool retry)
    {
        if (_menuMasked || (_menuMaskAttempted && !retry))
            return;
        _menuMaskAttempted = true;
        try
        {
            Native.SendKey(0xE8);
            _menuMasked = true;
        }
        catch (Exception ex)
        {
            if (!_menuFaultReported)
            {
                _menuFaultReported = true;
                PostWarning($"Could not mask the Windows/Alt menu after a consumed chord: {ex.Message}");
            }
        }
    }

    private static bool IsDown(int key) => (Native.GetAsyncKeyState(key) & 0x8000) != 0;

    private static Modifiers ReadModifiers(int changedKey = -1, bool down = false)
    {
        bool Held(int key) => key == changedKey ? down : IsDown(key);
        Modifiers result = 0;
        if (Held(0x5B) || Held(0x5C)) result |= Modifiers.Windows;
        if (Held(0xA4) || Held(0xA5)) result |= Modifiers.Alt;
        if (Held(0xA2) || Held(0xA3)) result |= Modifiers.Control;
        if (Held(0xA0) || Held(0xA1)) result |= Modifiers.Shift;
        return result;
    }

    private static int NormalizeModifier(Native.KeyboardData data) => data.VirtualKey switch
    {
        0x10 => data.ScanCode == 0x36 ? 0xA1 : 0xA0,
        0x11 => (data.Flags & 1) != 0 ? 0xA3 : 0xA2,
        0x12 => (data.Flags & 1) != 0 ? 0xA5 : 0xA4,
        _ => (int)data.VirtualKey
    };

    private static int KeySlot(Native.KeyboardData data) => data.ScanCode == 0
        ? 512 + (int)data.VirtualKey
        : (int)(data.ScanCode & 0xFF) | ((data.Flags & 1) != 0 ? 256 : 0);

    private static int Chord(Modifiers modifiers, int key) => ((int)modifiers << 16) | key;

    private void PostWarning(string warning)
    {
        try
        {
            _host.Post(() => AddRuntimeWarning(warning));
        }
        catch
        {
            // No exception may cross a native hook boundary, including a failing dispatcher.
        }
    }

    private void AddRuntimeWarning(string warning)
    {
        if (_runtimeWarnings.Count < 32 && !_runtimeWarnings.Contains(warning, StringComparer.Ordinal))
        {
            _runtimeWarnings.Add(warning);
            UpdateWarnings();
        }
    }

    private void UpdateWarnings() => Warnings = Array.AsReadOnly(_bindings.Warnings.Concat(_runtimeWarnings).ToArray());

    private static BindingSet CompileBindings(SwayConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var modes = new Dictionary<string, ModeBindings>(StringComparer.Ordinal);
        var warnings = new List<string>();
        int order = 0;
        foreach (KeyBinding binding in config.Bindings)
        {
            string location = $"{binding.SourcePath}:{binding.LineNumber}: '{binding.Keys}'";
            if (binding.Locked)
                warnings.Add($"{location}: --locked applies only on the normal Windows desktop; hooks do not operate on the lock/UAC secure desktop.");
            if (!TryParseChord(binding.Keys, out Modifiers modifiers, out KeySpec key, out string error))
            {
                warnings.Add($"{location}: {error}");
                continue;
            }
            if ((modifiers & Modifiers.Windows) != 0 && (key.VirtualKey == 0x4C || key.Symbol == 'l'))
            {
                warnings.Add($"{location}: Win+L is reserved by Windows for system lock; this binding is skipped. Use Win+Right for focus right.");
                continue;
            }
            if (modifiers == EmergencyModifiers && key.VirtualKey == 0x7B)
            {
                warnings.Add($"{location}: Ctrl+Alt+Shift+F12 is reserved for the global emergency exit; this binding is skipped.");
                continue;
            }
            if (binding.ToCode && key.Scan == 0)
            {
                warnings.Add($"{location}: --to-code has no physical US scan-code mapping for this symbol; this binding is skipped.");
                continue;
            }
            if (string.IsNullOrWhiteSpace(binding.Mode) || string.IsNullOrWhiteSpace(binding.Command))
            {
                warnings.Add($"{location}: a binding must have a mode and a command; this binding is skipped.");
                continue;
            }
            if (!modes.TryGetValue(binding.Mode, out ModeBindings? mode))
            {
                mode = new ModeBindings();
                modes.Add(binding.Mode, mode);
            }
            Dictionary<int, BindingPair> index = binding.ToCode ? mode.Scans : key.Symbol != '\0' ? mode.Symbols : mode.VirtualKeys;
            int value = binding.ToCode ? key.Scan : key.Symbol != '\0' ? key.Symbol : key.VirtualKey;
            int chord = Chord(modifiers, value);
            index.TryGetValue(chord, out BindingPair pair);
            if ((binding.Release ? pair.Release : pair.Press) is not null)
                warnings.Add($"{location}: duplicate binding in mode '{binding.Mode}'; the later binding takes precedence.");
            var compiled = new CompiledBinding(binding.Mode, binding.Command, binding.NoRepeat, order++);
            if (binding.Release) pair.Release = compiled;
            else pair.Press = compiled;
            index[chord] = pair;
            if (!binding.ToCode && key.Symbol != '\0')
                mode.SymbolModifiers |= 1 << (int)modifiers;
        }

        string floating = config.Settings.FloatingModifier.Trim();
        if (floating.EndsWith(" normal", StringComparison.OrdinalIgnoreCase))
            floating = floating[..^7].TrimEnd();
        Modifiers floatingModifiers = 0;
        if (!string.Equals(floating, "none", StringComparison.OrdinalIgnoreCase))
        {
            foreach (string part in floating.Split('+', StringSplitOptions.TrimEntries))
            {
                if (!TryModifier(part, out Modifiers modifier))
                {
                    warnings.Add($"Floating modifier '{config.Settings.FloatingModifier}' is unsupported; modifier dragging is disabled.");
                    floatingModifiers = 0;
                    break;
                }
                floatingModifiers |= modifier;
            }
        }
        int minWidth = Math.Clamp(config.Settings.MinimumFloatingWidth, 1, MaxFloatingSize);
        int minHeight = Math.Clamp(config.Settings.MinimumFloatingHeight, 1, MaxFloatingSize);
        int maxWidth = config.Settings.MaximumFloatingWidth <= 0 ? MaxFloatingSize :
            Math.Clamp(config.Settings.MaximumFloatingWidth, minWidth, MaxFloatingSize);
        int maxHeight = config.Settings.MaximumFloatingHeight <= 0 ? MaxFloatingSize :
            Math.Clamp(config.Settings.MaximumFloatingHeight, minHeight, MaxFloatingSize);
        return new BindingSet(modes, floatingModifiers, minWidth, minHeight, maxWidth, maxHeight, warnings.AsReadOnly());
    }

    private static bool TryParseChord(string keys, out Modifiers modifiers, out KeySpec key, out string error)
    {
        modifiers = 0;
        key = default;
        error = "A binding must contain exactly one non-modifier key (use the name 'plus' for '+').";
        string? keyName = null;
        foreach (string part in keys.Split('+', StringSplitOptions.TrimEntries))
        {
            if (TryModifier(part, out Modifiers modifier))
                modifiers |= modifier;
            else if (part.Length == 0 || keyName is not null)
                return false;
            else
                keyName = part;
        }
        return keyName is not null && TryKey(keyName, out key, out error);
    }

    private static bool TryModifier(string name, out Modifiers modifier)
    {
        modifier = name.ToLowerInvariant() switch
        {
            "mod4" or "win" or "super" => Modifiers.Windows,
            "mod1" or "alt" => Modifiers.Alt,
            "ctrl" or "control" => Modifiers.Control,
            "shift" => Modifiers.Shift,
            _ => 0
        };
        return modifier != 0;
    }

    private static bool TryKey(string name, out KeySpec key, out string error)
    {
        key = default;
        error = $"Unsupported Linux key name '{name}'; this binding is skipped.";
        string normalized = name.ToLowerInvariant();
        if (normalized is "xf86audiomicmute" or "xf86monbrightnessup" or "xf86monbrightnessdown" or
            "xf86kbdbrightnessup" or "xf86kbdbrightnessdown" or "xf86kbdlightonoff")
        {
            error = $"'{name}' has no reliable ordinary Windows keyboard virtual-key representation; this binding is skipped. Use its Windows override instead.";
            return false;
        }
        if (normalized.Length > 1 && normalized[0] == 'f' &&
            int.TryParse(normalized.AsSpan(1), NumberStyles.None, CultureInfo.InvariantCulture, out int function) && function is >= 1 and <= 24)
        {
            int scan = function <= 10 ? 0x3A + function : function <= 12 ? 0x57 + function - 11 :
                function == 24 ? 0x76 : 0x64 + function - 13;
            key = new KeySpec(0x6F + function, scan, '\0');
            return true;
        }
        char symbol = normalized.Length == 1 ? normalized[0] : normalized switch
        {
            "space" => ' ', "minus" => '-', "equal" => '=', "plus" => '+', "underscore" => '_',
            "comma" => ',', "period" => '.', "slash" => '/', "backslash" => '\\',
            "semicolon" => ';', "colon" => ':', "apostrophe" => '\'', "quotedbl" => '"',
            "grave" or "quoteleft" => '`', "asciitilde" => '~', "bracketleft" => '[', "bracketright" => ']',
            "braceleft" => '{', "braceright" => '}', "exclam" => '!', "at" => '@', "numbersign" => '#',
            "dollar" => '$', "percent" => '%', "asciicircum" => '^', "ampersand" => '&', "asterisk" => '*',
            "parenleft" => '(', "parenright" => ')', "less" => '<', "greater" => '>', "question" => '?', "bar" => '|',
            _ => '\0'
        };
        if (symbol != '\0')
        {
            int scan = SymbolScan(symbol);
            int vk = symbol is >= 'a' and <= 'z' ? symbol - 'a' + 0x41 :
                symbol is >= '0' and <= '9' ? symbol : 0;
            key = new KeySpec(vk, scan, symbol);
            return true;
        }
        key = normalized switch
        {
            "return" or "enter" => new(0x0D, 0x1C, '\0'),
            "escape" or "esc" => new(0x1B, 0x01, '\0'),
            "backspace" => new(0x08, 0x0E, '\0'),
            "tab" or "iso_left_tab" => new(0x09, 0x0F, '\0'),
            "left" => new(0x25, 0x14B, '\0'), "right" => new(0x27, 0x14D, '\0'),
            "up" => new(0x26, 0x148, '\0'), "down" => new(0x28, 0x150, '\0'),
            "print" or "printscreen" or "print_screen" => new(0x2C, 0x137, '\0'),
            "home" => new(0x24, 0x147, '\0'), "end" => new(0x23, 0x14F, '\0'),
            "prior" or "page_up" or "pageup" => new(0x21, 0x149, '\0'),
            "next" or "page_down" or "pagedown" => new(0x22, 0x151, '\0'),
            "insert" => new(0x2D, 0x152, '\0'), "delete" => new(0x2E, 0x153, '\0'),
            "menu" => new(0x5D, 0x15D, '\0'), "pause" or "break" => new(0x13, 0x45, '\0'),
            "caps_lock" => new(0x14, 0x3A, '\0'), "num_lock" => new(0x90, 0x145, '\0'),
            "scroll_lock" => new(0x91, 0x46, '\0'),
            "kp_enter" => new(0x0D, 0x11C, '\0'),
            "xf86audioraisevolume" or "volumeup" => new(0xAF, 0x130, '\0'),
            "xf86audiolowervolume" or "volumedown" => new(0xAE, 0x12E, '\0'),
            "xf86audiomute" or "volumemute" => new(0xAD, 0x120, '\0'),
            "xf86audioplay" or "xf86audiopause" => new(0xB3, 0x122, '\0'),
            "xf86audiostop" => new(0xB2, 0x124, '\0'),
            "xf86audionext" => new(0xB0, 0x119, '\0'),
            "xf86audioprev" => new(0xB1, 0x110, '\0'),
            _ => default
        };
        return key.VirtualKey != 0;
    }

    private static int SymbolScan(char symbol)
    {
        if (symbol is >= 'a' and <= 'z')
            return LetterScans[symbol - 'a'];
        return symbol switch
        {
            '1' or '!' => 0x02, '2' or '@' => 0x03, '3' or '#' => 0x04, '4' or '$' => 0x05,
            '5' or '%' => 0x06, '6' or '^' => 0x07, '7' or '&' => 0x08, '8' or '*' => 0x09,
            '9' or '(' => 0x0A, '0' or ')' => 0x0B, '-' or '_' => 0x0C, '=' or '+' => 0x0D,
            '[' or '{' => 0x1A, ']' or '}' => 0x1B, ';' or ':' => 0x27, '\'' or '"' => 0x28,
            '`' or '~' => 0x29, '\\' or '|' => 0x2B, ',' or '<' => 0x33, '.' or '>' => 0x34,
            '/' or '?' => 0x35, ' ' => 0x39,
            _ => 0
        };
    }

    [Flags]
    private enum Modifiers { Windows = 1, Alt = 2, Control = 4, Shift = 8 }

    private readonly record struct KeySpec(int VirtualKey, int Scan, char Symbol);
    private sealed record CompiledBinding(string Mode, string Command, bool NoRepeat, int Order);
    private sealed record KeyCapture(CompiledBinding? Press, CompiledBinding? Release,
        Modifiers Modifiers, int Generation, bool Emergency);
    private struct BindingPair
    {
        public CompiledBinding? Press;
        public CompiledBinding? Release;
    }

    private sealed class ModeBindings
    {
        public readonly Dictionary<int, BindingPair> Scans = [];
        public readonly Dictionary<int, BindingPair> VirtualKeys = [];
        public readonly Dictionary<int, BindingPair> Symbols = [];
        public int SymbolModifiers;
    }

    private sealed record BindingSet(Dictionary<string, ModeBindings> Modes, Modifiers FloatingModifiers,
        int MinWidth, int MinHeight, int MaxWidth, int MaxHeight, IReadOnlyList<string> Warnings);

    private sealed class DragSession(nint window, Rectangle bounds, Native.Point origin, int button, Modifiers modifiers)
    {
        public readonly nint Window = window;
        public readonly Rectangle InitialBounds = bounds;
        public readonly Native.Point Origin = origin;
        public readonly int Button = button;
        public readonly Modifiers Modifiers = modifiers;
        public readonly bool ResizeLeft = origin.X < (long)bounds.X + bounds.Width / 2;
        public readonly bool ResizeTop = origin.Y < (long)bounds.Y + bounds.Height / 2;
        public Rectangle LastBounds = bounds;
        public bool Cancelled;
    }

    private readonly record struct DragUpdate(DragSession Drag, Rectangle Bounds);
}

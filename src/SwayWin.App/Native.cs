using System.ComponentModel;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace SwayWin;

internal static class Native
{
    internal const int WhKeyboardLl = 13;
    internal const int WhMouseLl = 14;

    internal const uint WmNull = 0x0000;
    internal const uint WmDestroy = 0x0002;
    internal const uint WmMove = 0x0003;
    internal const uint WmSize = 0x0005;
    internal const uint WmActivate = 0x0006;
    internal const uint WmSetfocus = 0x0007;
    internal const uint WmKillfocus = 0x0008;
    internal const uint WmClose = 0x0010;
    internal const uint WmQuit = 0x0012;
    internal const uint WmShowwindow = 0x0018;
    internal const uint WmWindowposchanging = 0x0046;
    internal const uint WmWindowposchanged = 0x0047;
    internal const uint WmInput = 0x00FF;
    internal const uint WmKeydown = 0x0100;
    internal const uint WmKeyup = 0x0101;
    internal const uint WmChar = 0x0102;
    internal const uint WmDeadchar = 0x0103;
    internal const uint WmSyskeydown = 0x0104;
    internal const uint WmSyskeyup = 0x0105;
    internal const uint WmSyschar = 0x0106;
    internal const uint WmSysdeadchar = 0x0107;
    internal const uint WmUnichar = 0x0109;
    internal const uint WmSyscommand = 0x0112;
    internal const uint WmMousemove = 0x0200;
    internal const uint WmLbuttondown = 0x0201;
    internal const uint WmLbuttonup = 0x0202;
    internal const uint WmLbuttondblclk = 0x0203;
    internal const uint WmRbuttondown = 0x0204;
    internal const uint WmRbuttonup = 0x0205;
    internal const uint WmRbuttondblclk = 0x0206;
    internal const uint WmMbuttondown = 0x0207;
    internal const uint WmMbuttonup = 0x0208;
    internal const uint WmMbuttondblclk = 0x0209;
    internal const uint WmMousewheel = 0x020A;
    internal const uint WmXbuttondown = 0x020B;
    internal const uint WmXbuttonup = 0x020C;
    internal const uint WmXbuttondblclk = 0x020D;
    internal const uint WmMousehwheel = 0x020E;
    internal const uint WmHotkey = 0x0312;
    internal const uint WmApp = 0x8000;

    internal const uint LlkhfExtended = 0x01;
    internal const uint LlkhfLowerIlInjected = 0x02;
    internal const uint LlkhfInjected = 0x10;
    internal const uint LlkhfAltdown = 0x20;
    internal const uint LlkhfUp = 0x80;
    internal const uint LlmhfInjected = 0x01;
    internal const uint LlmhfLowerIlInjected = 0x02;

    internal const int ObjidWindow = 0;
    internal const uint EventSystemForeground = 0x0003;
    internal const uint EventSystemMoveSizeEnd = 0x000B;
    internal const uint EventObjectDestroy = 0x8001;
    internal const uint EventObjectShow = 0x8002;
    internal const uint EventObjectHide = 0x8003;
    internal const uint EventObjectLocationChange = 0x800B;
    internal const uint EventObjectNameChange = 0x800C;
    internal const uint WineventOutofcontext = 0;
    internal const uint WineventSkipownprocess = 2;

    internal const uint GaRoot = 2;
    internal const uint GwOwner = 4;

    internal const int SwHide = 0;
    internal const int SwShowminimized = 2;
    internal const int SwShowmaximized = 3;
    internal const int SwShownoactivate = 4;
    internal const int SwShowna = 8;
    internal const int SwShow = 5;
    internal const int SwRestore = 9;

    internal const long WsCaption = 0x00C00000;
    internal const long WsThickframe = 0x00040000;
    internal const long WsChild = 0x40000000;
    internal const long WsDisabled = 0x08000000;
    internal const long WsVisible = 0x10000000;
    internal const long WsPopup = 0x80000000;
    internal const long WsExToolwindow = 0x00000080;
    internal const long WsExAppwindow = 0x00040000;
    internal const long WsExNoactivate = 0x08000000;

    internal static readonly nint HwndTop = 0;
    internal static readonly nint HwndBottom = 1;
    internal static readonly nint HwndTopmost = -1;
    internal static readonly nint HwndNotopmost = -2;
    internal static readonly nint HwndMessage = -3;
    internal static readonly nint HwndBroadcast = 0xFFFF;

    internal const uint SwpNosize = 0x0001;
    internal const uint SwpNomove = 0x0002;
    internal const uint SwpNozorder = 0x0004;
    internal const uint SwpNoredraw = 0x0008;
    internal const uint SwpNoactivate = 0x0010;
    internal const uint SwpFramechanged = 0x0020;
    internal const uint SwpShowwindow = 0x0040;
    internal const uint SwpHidewindow = 0x0080;
    internal const uint SwpNocopybits = 0x0100;
    internal const uint SwpNoownerzorder = 0x0200;
    internal const uint SwpNosendchanging = 0x0400;
    internal const uint SwpDefererase = 0x2000;
    internal const uint SwpAsyncwindowpos = 0x4000;

    internal const int DwmwaExtendedFrameBounds = 9;
    internal const int DwmwaCloaked = 14;
    internal const int DwmwaUseImmersiveDarkMode = 20;
    internal const int DwmwaWindowCornerPreference = 33;
    internal const int DwmwaBorderColor = 34;
    internal const int DwmwaCaptionColor = 35;
    internal const int DwmwaTextColor = 36;
    internal const int DwmColorDefault = unchecked((int)0xFFFFFFFF);
    internal const int DwmColorNone = unchecked((int)0xFFFFFFFE);

    internal const uint MapvkVkToVsc = 0;
    internal const uint MapvkVscToVk = 1;
    internal const uint MapvkVkToChar = 2;
    internal const uint MapvkVscToVkEx = 3;
    internal const uint MapvkVkToVscEx = 4;
    internal const uint ToUnicodeNoStateChange = 4;
    internal const uint AttachParentProcess = 0xFFFFFFFF;

    private const int GwlStyle = -16;
    private const int GwlExstyle = -20;
    private const int MaxTextLength = 4096;
    private const uint InputKeyboard = 1;
    private const uint KeyeventfExtendedkey = 0x0001;
    private const uint KeyeventfKeyup = 0x0002;

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal delegate bool EnumWindowsProc(nint hwnd, nint parameter);

    // Keep hook delegates rooted until their corresponding hook has been unhooked.
    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate nint HookProc(int code, nint wParam, nint lParam);

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    internal delegate void WinEventProc(nint hook, uint eventType, nint hwnd,
        int objectId, int childId, uint eventThread, uint eventTime);

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;

        public readonly Rectangle ToRectangle() => Rectangle.FromLTRB(Left, Top, Right, Bottom);

        public static Rect FromRectangle(Rectangle r) => new()
        {
            Left = r.Left,
            Top = r.Top,
            Right = r.Right,
            Bottom = r.Bottom
        };
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WindowPlacement
    {
        public uint Length;
        public uint Flags;
        public uint ShowCmd;
        public Point MinPosition;
        public Point MaxPosition;
        public Rect NormalPosition;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct KeyboardData
    {
        public uint VirtualKey;
        public uint ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MouseData
    {
        public Point Position;
        public uint MouseDataValue;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Input
    {
        public uint Type;
        public InputData Data;
    }

    // Including MOUSEINPUT gives INPUT its native size and alignment on both x86 and x64.
    [StructLayout(LayoutKind.Explicit)]
    private struct InputData
    {
        [FieldOffset(0)] public MouseInput Mouse;
        [FieldOffset(0)] public KeyboardInput Keyboard;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MouseInput
    {
        public int X;
        public int Y;
        public uint MouseData;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KeyboardInput
    {
        public ushort VirtualKey;
        public ushort ScanCode;
        public uint Flags;
        public uint Time;
        public nuint ExtraInfo;
    }

    internal static string GetText(nint hwnd)
    {
        var buffer = new StringBuilder(MaxTextLength + 1);
        return GetWindowText(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    internal static string GetClass(nint hwnd)
    {
        var buffer = new StringBuilder(256);
        return GetClassName(hwnd, buffer, buffer.Capacity) > 0 ? buffer.ToString() : string.Empty;
    }

    internal static uint GetProcessId(nint hwnd)
    {
        GetWindowThreadProcessId(hwnd, out uint processId);
        return processId;
    }

    internal static long GetStyle(nint hwnd) => GetWindowStyle(hwnd, GwlStyle);

    internal static long GetExtendedStyle(nint hwnd) => GetWindowStyle(hwnd, GwlExstyle);

    private static long GetWindowStyle(nint hwnd, int index)
    {
        nint value = nint.Size == 8 ? GetWindowLongPtr(hwnd, index) : GetWindowLong(hwnd, index);
        return unchecked((uint)value);
    }

    internal static void SetStyle(nint hwnd, long style)
    {
        Marshal.SetLastPInvokeError(0);
        nint previous = nint.Size == 8
            ? SetWindowLongPtr(hwnd, GwlStyle, unchecked((nint)(int)style))
            : SetWindowLong(hwnd, GwlStyle, unchecked((int)style));
        int error = Marshal.GetLastWin32Error();
        if (previous == 0 && error != 0)
            throw new Win32Exception(error, "Could not set the window style.");

        if (!SetWindowPos(hwnd, HwndTop, 0, 0, 0, 0,
                SwpFramechanged | SwpNomove | SwpNosize | SwpNozorder | SwpNoactivate | SwpNoownerzorder))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not refresh the window frame.");
    }

    internal static Rectangle GetBounds(nint hwnd, bool visibleFrame = false)
    {
        if (visibleFrame && DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds,
                out Rect frame, Marshal.SizeOf<Rect>()) >= 0 &&
            frame.Right > frame.Left && frame.Bottom > frame.Top)
            return frame.ToRectangle();

        return GetWindowRect(hwnd, out Rect rect) ? rect.ToRectangle() : Rectangle.Empty;
    }

    internal static WindowPlacement GetPlacement(nint hwnd)
    {
        var placement = new WindowPlacement { Length = (uint)Marshal.SizeOf<WindowPlacement>() };
        if (!GetWindowPlacement(hwnd, ref placement))
            throw new Win32Exception(Marshal.GetLastWin32Error(), "Could not get the window placement.");
        return placement;
    }

    internal static bool SetPlacement(nint hwnd, WindowPlacement placement)
    {
        placement.Length = (uint)Marshal.SizeOf<WindowPlacement>();
        return SetWindowPlacement(hwnd, ref placement);
    }

    internal static bool SetBounds(nint hwnd, Rectangle rectangle, bool topMost = false)
    {
        if (rectangle.Width < 0 || rectangle.Height < 0)
            return false;

        uint flags = SwpNoactivate | SwpNoownerzorder;
        if (!topMost)
            flags |= SwpNozorder;

        return SetWindowPos(hwnd, topMost ? HwndTopmost : HwndTop,
            rectangle.X, rectangle.Y, rectangle.Width, rectangle.Height, flags);
    }

    internal static bool SetVisibleBounds(nint hwnd, Rectangle rectangle)
    {
        if (rectangle.Width < 0 || rectangle.Height < 0 || !GetWindowRect(hwnd, out Rect outer))
            return false;

        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out Rect frame, Marshal.SizeOf<Rect>()) < 0 ||
            frame.Right <= frame.Left || frame.Bottom <= frame.Top)
            return SetBounds(hwnd, rectangle);

        long x = (long)rectangle.X + outer.Left - frame.Left;
        long y = (long)rectangle.Y + outer.Top - frame.Top;
        long width = (long)rectangle.Width + frame.Left - outer.Left + outer.Right - frame.Right;
        long height = (long)rectangle.Height + frame.Top - outer.Top + outer.Bottom - frame.Bottom;
        if (x < int.MinValue || x > int.MaxValue || y < int.MinValue || y > int.MaxValue ||
            width < 0 || width > int.MaxValue || height < 0 || height > int.MaxValue)
            return false;

        return SetBounds(hwnd, new Rectangle((int)x, (int)y, (int)width, (int)height));
    }

    internal static bool FocusWindow(nint hwnd)
    {
        if (!IsWindow(hwnd) || IsHungAppWindow(hwnd))
            return false;
        if (IsIconic(hwnd))
            ShowWindow(hwnd, SwRestore);
        if (GetForegroundWindow() == hwnd || SetForegroundWindow(hwnd))
            return true;

        uint currentThread = GetCurrentThreadId();
        uint foregroundThread = GetWindowThreadProcessId(GetForegroundWindow(), out _);
        if (foregroundThread == 0 || foregroundThread == currentThread)
            return false;

        bool attached = false;
        try
        {
            attached = AttachThreadInput(currentThread, foregroundThread, true);
            return attached && (SetForegroundWindow(hwnd) || GetForegroundWindow() == hwnd);
        }
        finally
        {
            if (attached)
                AttachThreadInput(currentThread, foregroundThread, false);
        }
    }

    internal static int DwmGetInt(nint hwnd, int attribute, int fallback) =>
        DwmGetWindowAttribute(hwnd, attribute, out int value, sizeof(int)) >= 0 ? value : fallback;

    internal static bool DwmSetInt(nint hwnd, int attribute, int value) =>
        DwmSetWindowAttribute(hwnd, attribute, ref value, sizeof(int)) >= 0;

    internal static int ColorRef(Color color) => color.R | (color.G << 8) | (color.B << 16);

    internal static void SendKey(ushort key) => SendKeys(key);

    internal static void SendKeys(params ushort[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        if (keys.Length == 0)
            return;

        nint layout = GetKeyboardLayout(GetWindowThreadProcessId(GetForegroundWindow(), out _));
        var inputs = new Input[checked(keys.Length * 2)];
        for (int i = 0; i < keys.Length; i++)
        {
            if (keys[i] is 0 or > 0xFE)
                throw new ArgumentOutOfRangeException(nameof(keys), "Virtual-key codes must be between 1 and 254.");
            inputs[i] = CreateKeyInput(keys[i], false, layout);
            inputs[inputs.Length - i - 1] = CreateKeyInput(keys[i], true, layout);
        }

        // SendInput marks the events as injected; one batch keeps the chord ordering intact.
        uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<Input>());
        if (sent == inputs.Length)
            return;

        int error = Marshal.GetLastWin32Error();
        int outstanding = sent <= keys.Length ? (int)sent : inputs.Length - (int)sent;
        if (outstanding > 0)
        {
            // Release only the downs left outstanding by a partially accepted batch.
            var releases = new Input[outstanding];
            Array.Copy(inputs, inputs.Length - outstanding, releases, 0, outstanding);
            SendInput((uint)releases.Length, releases, Marshal.SizeOf<Input>());
        }

        throw new Win32Exception(error,
            "Keyboard input was not fully injected. Windows may block input across privilege levels.");
    }

    private static Input CreateKeyInput(ushort key, bool keyUp, nint layout)
    {
        uint scan = MapVirtualKeyEx(key, MapvkVkToVscEx, layout);
        return new Input
        {
            Type = InputKeyboard,
            Data = new InputData
            {
                Keyboard = new KeyboardInput
                {
                    VirtualKey = key,
                    ScanCode = (ushort)(scan & 0xFF),
                    Flags = ((scan & 0xFF00) == 0xE000 ? KeyeventfExtendedkey : 0) |
                        (keyUp ? KeyeventfKeyup : 0)
                }
            }
        };
    }

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool EnumWindows(EnumWindowsProc callback, nint parameter);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindow(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsWindowVisible(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsIconic(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsZoomed(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool IsHungAppWindow(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindowAsync(nint hwnd, int command);

    [DllImport("user32.dll", EntryPoint = "SendMessageTimeoutW", ExactSpelling = true, SetLastError = true)]
    private static extern nint SendMessageTimeout(nint hwnd, uint message, nint wParam, nint lParam,
        uint flags, uint timeout, out nuint result);

    internal static bool IsResponsiveWindow(nint hwnd) => IsWindow(hwnd) && !IsHungAppWindow(hwnd) &&
        SendMessageTimeout(hwnd, 0, 0, 0, 0x23, 100, out _) != 0;

    // ShowWindow returns the previous visibility, not a success indicator.
    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool ShowWindow(nint hwnd, int command);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint GetForegroundWindow();

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint GetWindow(nint hwnd, uint command);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint GetAncestor(nint hwnd, uint flags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetWindowPos(nint hwnd, nint insertAfter,
        int x, int y, int width, int height, uint flags);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetWindowRect(nint hwnd, out Rect rect);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    internal static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module,
        WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll", EntryPoint = "SetWindowsHookExW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    internal static extern nint SetWindowsHookEx(int hookId, HookProc callback, nint module, uint threadId);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool UnhookWindowsHookEx(nint hook);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern short GetAsyncKeyState(int virtualKey);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern short GetKeyState(int virtualKey);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool GetCursorPos(out Point point);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint WindowFromPoint(Point point);

    [DllImport("user32.dll", ExactSpelling = true)]
    internal static extern nint GetKeyboardLayout(uint threadId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern int ToUnicodeEx(uint vk, uint scan,
        [In, MarshalAs(UnmanagedType.LPArray, SizeConst = 256)] byte[] state,
        [Out] StringBuilder buffer, int cch, uint flags, nint layout);

    [DllImport("user32.dll", EntryPoint = "MapVirtualKeyExW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    internal static extern uint MapVirtualKeyEx(uint code, uint mapType, nint layout);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool PostMessage(nint hwnd, uint message, nint wParam, nint lParam);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool AttachConsole(uint processId);

    [DllImport("kernel32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static extern bool FreeConsole();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    internal static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", EntryPoint = "GetModuleHandleW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    internal static extern nint GetModuleHandle(string? name);

    [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetWindowText(nint hwnd, [Out] StringBuilder buffer, int capacity);

    [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern int GetClassName(nint hwnd, [Out] StringBuilder buffer, int capacity);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern nint GetWindowLongPtr(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern int GetWindowLong(nint hwnd, int index);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern nint SetWindowLongPtr(nint hwnd, int index, nint value);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongW", CharSet = CharSet.Unicode,
        ExactSpelling = true, SetLastError = true)]
    private static extern int SetWindowLong(nint hwnd, int index, int value);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowPlacement(nint hwnd, ref WindowPlacement placement);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPlacement(nint hwnd, [In] ref WindowPlacement placement);

    [DllImport("user32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(nint hwnd);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AttachThreadInput(uint attachThread, uint attachToThread,
        [MarshalAs(UnmanagedType.Bool)] bool attach);

    [DllImport("user32.dll", ExactSpelling = true, SetLastError = true)]
    private static extern uint SendInput(uint count, [In] Input[] inputs, int size);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out Rect value, int size);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [DllImport("dwmapi.dll", ExactSpelling = true)]
    private static extern int DwmSetWindowAttribute(nint hwnd, int attribute, [In] ref int value, int size);
}

internal sealed class NativeWorkQueue
{
    private readonly object gate = new();
    private Action? pending;
    private bool running;
    private bool cancelled;
    private Task completion = Task.CompletedTask;
    internal bool IsBusy { get { lock (gate) return running || pending is not null; } }

    internal void Submit(Action operation)
    {
        lock (gate)
        {
            if (cancelled) return;
            pending = operation;
            if (running) return;
            running = true;
            completion = Task.Run(Run);
        }
    }

    private void Run()
    {
        while (true)
        {
            Action operation;
            lock (gate)
            {
                if (cancelled || pending is null) { pending = null; running = false; return; }
                operation = pending;
                pending = null;
            }
            try { operation(); }
            catch (Exception exception) { AppLog.Write($"Native worker: {exception.Message}"); }
        }
    }

    internal Task Cancel()
    {
        lock (gate) { cancelled = true; pending = null; return completion; }
    }
}

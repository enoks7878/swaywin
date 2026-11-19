namespace SwayWin;

internal static class Theme
{
    internal static readonly Color Background = Color.Black;
    internal static readonly Color Surface = Color.FromArgb(25, 25, 25);
    internal static readonly Color Accent = Color.FromArgb(0, 120, 212);
    internal static readonly Color Text = Color.FromArgb(230, 230, 230);
    internal static readonly Color Muted = Color.FromArgb(125, 125, 125);

    internal static void ConfigureFrame(nint hwnd, long originalStyle, bool stripTitlebar)
    {
        if (Native.IsZoomed(hwnd)) Native.ShowWindow(hwnd, Native.SwShownoactivate);
        long current = Native.GetStyle(hwnd);
        const long frame = Native.WsCaption | Native.WsThickframe;
        long style = (current & ~frame) | (stripTitlebar ? 0 : originalStyle & frame);
        if (style != current) Native.SetStyle(hwnd, style);
    }

    internal static void Apply(nint hwnd, bool focused, bool border)
    {
        Native.DwmSetInt(hwnd, Native.DwmwaUseImmersiveDarkMode, 1);
        Native.DwmSetInt(hwnd, Native.DwmwaWindowCornerPreference, 1);
        Native.DwmSetInt(hwnd, Native.DwmwaBorderColor, border ? Native.ColorRef(focused ? Accent : Surface) : Native.DwmColorNone);
        Native.DwmSetInt(hwnd, Native.DwmwaCaptionColor, Native.ColorRef(Background));
        Native.DwmSetInt(hwnd, Native.DwmwaTextColor, Native.ColorRef(Text));
    }
}

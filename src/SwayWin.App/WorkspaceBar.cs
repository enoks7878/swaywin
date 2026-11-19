using System.Globalization;
using System.Runtime.InteropServices;
using SwayWin.Core.Configuration;
using SwayWin.Core.Layout;

namespace SwayWin;

internal sealed class WorkspaceBar : Form
{
    private readonly string outputName;
    private readonly Func<WindowManagerState> state;
    private readonly Func<string> mode;
    private readonly Action<string, string> switchWorkspace;
    private readonly Action workAreaChanged;
    private readonly System.Windows.Forms.Timer clock = new() { Interval = 10000 };
    private readonly List<(Rectangle Bounds, string Name)> buttons = [];
    private readonly uint callbackMessage = RegisterWindowMessage("SwayWin.AppBar.Callback");
    private readonly uint taskbarCreated = RegisterWindowMessage("TaskbarCreated");
    private BarSettings settings;
    private bool registered;
    private bool docking;
    private bool fullscreen;
    private Rectangle reservedBounds;

    internal WorkspaceBar(string outputName, BarSettings settings, Func<WindowManagerState> state,
        Func<string> mode, Action<string, string> switchWorkspace, Action workAreaChanged)
    {
        this.outputName = outputName;
        this.settings = settings;
        this.state = state;
        this.mode = mode;
        this.switchWorkspace = switchWorkspace;
        this.workAreaChanged = workAreaChanged;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Background;
        ForeColor = Theme.Text;
        Font = new Font("Segoe UI", 9);
        DoubleBuffered = true;
        clock.Tick += (_, _) => Invalidate();
        clock.Start();
    }

    internal string OutputName => outputName;
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(Native.WsExToolwindow | Native.WsExNoactivate);
            return parameters;
        }
    }

    internal void Configure(BarSettings updated)
    {
        settings = updated;
        DockBar();
    }

    internal void SetFullscreen(bool enabled)
    {
        if (fullscreen == enabled) return;
        fullscreen = enabled;
        if (enabled) { Unregister(); Hide(); }
        else DockBar();
    }

    internal void DockBar()
    {
        if (docking || fullscreen || IsDisposed || !settings.Enabled) return;
        Screen? screen = Screen.AllScreens.FirstOrDefault(item => item.DeviceName == outputName);
        if (screen is null) return;
        docking = true;
        try
        {
            AppBarData data = NewData();
            bool newRegistration = !registered;
            if (!registered)
            {
                data.CallbackMessage = callbackMessage;
                SHAppBarMessage(0, ref data);
                registered = true;
            }
            int height = Math.Clamp((int)Math.Round(settings.Height * DeviceDpi / 96.0), 18, Math.Max(18, screen.Bounds.Height / 3));
            bool top = settings.Position != "bottom";
            data.Edge = top ? 1u : 3u;
            data.Rectangle = Native.Rect.FromRectangle(screen.Bounds);
            SHAppBarMessage(2, ref data);
            if (top) data.Rectangle.Bottom = data.Rectangle.Top + height;
            else data.Rectangle.Top = data.Rectangle.Bottom - height;
            if (newRegistration || data.Rectangle.ToRectangle() != reservedBounds)
            {
                SHAppBarMessage(3, ref data);
                reservedBounds = data.Rectangle.ToRectangle();
            }
            Bounds = data.Rectangle.ToRectangle();
            if (!Visible) Show();
            Invalidate();
        }
        finally { docking = false; }
        workAreaChanged();
    }

    internal Rectangle ConstrainWorkArea(Rectangle area)
    {
        if (!registered || fullscreen || !Visible) return area;
        if (settings.Position != "bottom") return Rectangle.FromLTRB(area.Left, Math.Clamp(Bottom, area.Top, area.Bottom - 1), area.Right, area.Bottom);
        return Rectangle.FromLTRB(area.Left, area.Top, area.Right, Math.Clamp(Top, area.Top + 1, area.Bottom));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(Theme.Background);
        WindowManagerState manager = state();
        OutputState? output = manager.Outputs.FirstOrDefault(item => item.Name == outputName);
        if (output is null) return;
        int x = 0;
        buttons.Clear();
        IEnumerable<string> names = Enumerable.Range(1, 10).Select(number => number.ToString(CultureInfo.InvariantCulture))
            .Concat(manager.Workspaces.Keys.Where(name => !int.TryParse(name, out int number) || number < 1 || number > 10));
        foreach (string name in names)
        {
            int width = Math.Max(24 * DeviceDpi / 96, TextRenderer.MeasureText(name, Font).Width + 12);
            Rectangle button = new(x, 0, width, Height - 1);
            buttons.Add((button, name));
            bool active = name == output.ActiveWorkspace;
            bool occupied = manager.Workspaces.TryGetValue(name, out WorkspaceState? workspace) && workspace.AllWindows().Any();
            using SolidBrush fill = new(active ? Theme.Accent : Theme.Background);
            e.Graphics.FillRectangle(fill, button);
            TextRenderer.DrawText(e.Graphics, name, Font, button, active ? Color.White : occupied ? Theme.Text : Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            x += width;
        }
        string bindingMode = mode();
        if (bindingMode != "default")
        {
            int width = TextRenderer.MeasureText(bindingMode, Font).Width + 18;
            Rectangle label = new(x + 5, 0, width, Height - 1);
            using SolidBrush fill = new(Theme.Accent);
            e.Graphics.FillRectangle(fill, label);
            TextRenderer.DrawText(e.Graphics, bindingMode, Font, label, Color.White,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
            x += width + 10;
        }
        string time = DateTime.Now.ToString("ddd dd.MM  HH:mm", CultureInfo.CurrentCulture);
        int timeWidth = TextRenderer.MeasureText(time, Font).Width + 20;
        Rectangle timeBounds = new(Width - timeWidth, 0, timeWidth - 8, Height - 1);
        TextRenderer.DrawText(e.Graphics, time, Font, timeBounds, Theme.Text,
            TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        string title = manager.FocusedWindow is { } focused && manager.WorkspaceOf(focused)?.Name == output.ActiveWorkspace ? focused.Title : "SwayWin";
        Rectangle titleBounds = new(x + 14, 0, Math.Max(0, Width - timeWidth - x - 28), Height - 1);
        TextRenderer.DrawText(e.Graphics, title, Font, titleBounds, Theme.Muted,
            TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
        using Pen bottomLine = new(Theme.Surface);
        e.Graphics.DrawLine(bottomLine, 0, Height - 1, Width, Height - 1);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button != MouseButtons.Left) return;
        foreach ((Rectangle bounds, string name) in buttons)
            if (bounds.Contains(e.Location)) { switchWorkspace(outputName, name); return; }
    }

    protected override void WndProc(ref Message message)
    {
        if ((uint)message.Msg == callbackMessage && message.WParam == 1 && !docking)
            BeginInvoke(DockBar);
        if ((uint)message.Msg == callbackMessage && message.WParam == 2)
        {
            bool opened = message.LParam != 0;
            TopMost = !opened;
            Native.SetWindowPos(Handle, opened ? (nint)1 : Native.HwndTopmost, 0, 0, 0, 0,
                Native.SwpNomove | Native.SwpNosize | Native.SwpNoactivate);
        }
        if ((uint)message.Msg == taskbarCreated)
        {
            registered = false;
            BeginInvoke(DockBar);
        }
        base.WndProc(ref message);
    }

    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!IsDisposed) BeginInvoke(DockBar);
    }

    private AppBarData NewData() => new() { Size = (uint)Marshal.SizeOf<AppBarData>(), Window = Handle };

    private void Unregister()
    {
        if (!registered) return;
        AppBarData data = NewData();
        SHAppBarMessage(1, ref data);
        registered = false;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { clock.Dispose(); Unregister(); Font.Dispose(); }
        base.Dispose(disposing);
    }

    [StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct AppBarData
    {
        public uint Size;
        public nint Window;
        public uint CallbackMessage;
        public uint Edge;
        public Native.Rect Rectangle;
        public nint Parameter;
    }

    [DllImport("shell32.dll", ExactSpelling = true)]
    private static extern nuint SHAppBarMessage(uint message, ref AppBarData data);

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint RegisterWindowMessage(string text);
}

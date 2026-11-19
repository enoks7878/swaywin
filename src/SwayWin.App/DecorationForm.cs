using SwayWin.Core.Layout;

namespace SwayWin;

internal sealed class DecorationForm : Form
{
    private readonly Action<long>? focusNode;
    private IReadOnlyList<TabLabel> tabs = [];
    private Color borderColor;
    private int borderWidth;
    private Region? ring;

    internal DecorationForm(bool border, Action<long>? focusNode = null)
    {
        IsBorder = border;
        this.focusNode = focusNode;
        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        TopMost = true;
        AutoScaleMode = AutoScaleMode.None;
        BackColor = Theme.Background;
        if (border) Opacity = .999;
        Font = new Font("Segoe UI", 9);
        DoubleBuffered = true;
    }

    private bool IsBorder { get; }
    protected override bool ShowWithoutActivation => true;
    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= (int)(Native.WsExToolwindow | Native.WsExNoactivate);
            if (IsBorder) parameters.ExStyle |= 0x20;
            return parameters;
        }
    }

    internal void UpdateBorder(Rectangle bounds, int width, bool focused)
    {
        Bounds = bounds;
        borderWidth = Math.Max(1, width);
        borderColor = focused ? Theme.Accent : Theme.Surface;
        Region? old = ring;
        ring = new Region(ClientRectangle);
        if (ClientSize.Width > borderWidth * 2 && ClientSize.Height > borderWidth * 2)
            ring.Exclude(new Rectangle(borderWidth, borderWidth, ClientSize.Width - borderWidth * 2, ClientSize.Height - borderWidth * 2));
        Region = ring;
        old?.Dispose();
        Invalidate();
    }

    internal void UpdateTabs(ContainerDecoration decoration)
    {
        tabs = decoration.Tabs;
        if (tabs.Count == 0) return;
        PixelRect first = tabs[0].Bounds;
        int right = tabs.Max(tab => tab.Bounds.Right);
        int bottom = tabs.Max(tab => tab.Bounds.Bottom);
        Bounds = new(first.X, first.Y, right - first.X, bottom - first.Y);
        Invalidate();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(IsBorder ? borderColor : Theme.Background);
        if (IsBorder) return;
        foreach (TabLabel tab in tabs)
        {
            Rectangle rectangle = new(tab.Bounds.X - Left, tab.Bounds.Y - Top, tab.Bounds.Width, tab.Bounds.Height);
            using SolidBrush background = new(tab.Active ? Theme.Accent : Theme.Surface);
            e.Graphics.FillRectangle(background, rectangle);
            TextRenderer.DrawText(e.Graphics, tab.Title, Font, Rectangle.Inflate(rectangle, -8, 0),
                tab.Active ? Color.White : Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            using Pen separator = new(Theme.Background);
            e.Graphics.DrawRectangle(separator, rectangle.X, rectangle.Y, Math.Max(0, rectangle.Width - 1), Math.Max(0, rectangle.Height - 1));
        }
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (IsBorder || e.Button != MouseButtons.Left) return;
        TabLabel? tab = tabs.FirstOrDefault(item => item.Bounds.Contains(Left + e.X, Top + e.Y));
        if (tab is not null) focusNode?.Invoke(tab.NodeId);
    }

    protected override void WndProc(ref Message message)
    {
        if (IsBorder && message.Msg == 0x0084) { message.Result = -1; return; }
        base.WndProc(ref message);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { Region = null; ring?.Dispose(); Font.Dispose(); }
        base.Dispose(disposing);
    }
}

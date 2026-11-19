using System.Drawing.Imaging;

namespace SwayWin;

internal sealed class RegionCaptureForm : Form
{
    private readonly Bitmap _frozen;
    private readonly Font _hintFont = new("Segoe UI", 10f);
    private Point _anchor;
    private Rectangle _selection;
    private bool _dragging;

    internal RegionCaptureForm(Rectangle displayBounds, Bitmap frozen)
    {
        _frozen = frozen;
        Text = "SwayWin Region Capture";
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.None;
        StartPosition = FormStartPosition.Manual;
        MaximumSize = displayBounds.Size;
        MinimumSize = displayBounds.Size;
        Bounds = displayBounds;
        ShowInTaskbar = false;
        TopMost = true;
        KeyPreview = true;
        DoubleBuffered = true;
        BackColor = Theme.Background;
        Cursor = Cursors.Cross;
    }

    protected override CreateParams CreateParams
    {
        get
        {
            CreateParams parameters = base.CreateParams;
            parameters.ExStyle |= 0x80;
            return parameters;
        }
    }

    internal Bitmap CopySelection()
    {
        if (DialogResult != DialogResult.OK || _selection.Width < 2 || _selection.Height < 2)
            throw new InvalidOperationException("No screenshot region was selected.");
        return _frozen.Clone(_selection, PixelFormat.Format32bppArgb);
    }

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        Activate();
        Focus();
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        base.OnPaint(e);
        e.Graphics.DrawImageUnscaled(_frozen, Point.Empty);
        using var dim = new SolidBrush(Color.FromArgb(204, Color.Black));
        e.Graphics.FillRectangle(dim, ClientRectangle);
        if (_selection.Width > 1 && _selection.Height > 1)
        {
            e.Graphics.DrawImage(_frozen, _selection, _selection, GraphicsUnit.Pixel);
            using var outline = new Pen(Theme.Accent, 2f);
            e.Graphics.DrawRectangle(outline, _selection.X + 1, _selection.Y + 1,
                _selection.Width - 2, _selection.Height - 2);
        }
        TextRenderer.DrawText(e.Graphics, "Drag to capture a region. Esc or right-click cancels.",
            _hintFont, new Point(20, 20), Theme.Text, Theme.Background, TextFormatFlags.NoPrefix);
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Right)
        {
            Cancel();
            return;
        }
        if (e.Button != MouseButtons.Left)
            return;
        _anchor = ClampPoint(e.Location);
        _selection = Rectangle.Empty;
        _dragging = true;
        Capture = true;
        Invalidate();
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging)
            return;
        UpdateSelection(e.Location);
        Invalidate();
    }

    protected override void OnMouseUp(MouseEventArgs e)
    {
        base.OnMouseUp(e);
        if (!_dragging || e.Button != MouseButtons.Left)
            return;
        UpdateSelection(e.Location);
        _dragging = false;
        Capture = false;
        if (_selection.Width >= 2 && _selection.Height >= 2)
        {
            DialogResult = DialogResult.OK;
            Close();
        }
        else
            Invalidate();
    }

    private Point ClampPoint(Point point) => new(
        Math.Clamp(point.X, 0, _frozen.Width), Math.Clamp(point.Y, 0, _frozen.Height));

    private void UpdateSelection(Point point)
    {
        point = ClampPoint(point);
        _selection = Rectangle.FromLTRB(Math.Min(_anchor.X, point.X), Math.Min(_anchor.Y, point.Y),
            Math.Max(_anchor.X, point.X), Math.Max(_anchor.Y, point.Y));
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (keyData != Keys.Escape)
            return base.ProcessCmdKey(ref msg, keyData);
        Cancel();
        return true;
    }

    private void Cancel()
    {
        _dragging = false;
        Capture = false;
        DialogResult = DialogResult.Cancel;
        Close();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _frozen.Dispose();
            _hintFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

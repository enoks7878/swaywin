using SwayWin.Core.Configuration;

namespace SwayWin.Core.Layout;

public readonly record struct PixelRect(int X, int Y, int Width, int Height)
{
    public int Right => X + Width;
    public int Bottom => Y + Height;
    public int CenterX => X + Width / 2;
    public int CenterY => Y + Height / 2;
    public bool Contains(int x, int y) => x >= X && y >= Y && x < Right && y < Bottom;

    public PixelRect Inset(int left, int top, int right, int bottom) =>
        new(X + left, Y + top, Math.Max(1, Width - left - right), Math.Max(1, Height - top - bottom));

    public PixelRect FitInside(PixelRect area)
    {
        int width = Math.Min(Math.Max(1, Width), area.Width);
        int height = Math.Min(Math.Max(1, Height), area.Height);
        return new(Math.Clamp(X, area.X, area.Right - width), Math.Clamp(Y, area.Y, area.Bottom - height), width, height);
    }
}

public enum LayoutKind { SplitHorizontal, SplitVertical, Tabbed, Stacked }
public enum Direction { Left, Down, Up, Right }
public enum ResizeAxis { Width, Height }

public abstract class LayoutNode
{
    private static long nextId;
    public long Id { get; } = Interlocked.Increment(ref nextId);
    public ContainerNode? Parent { get; internal set; }
    public double Weight { get; internal set; } = 1;
    public PixelRect Bounds { get; internal set; }
    public PixelRect FloatingBounds { get; internal set; }
    public bool IsFloating { get; internal set; }
    public bool IsScratchpad { get; internal set; }
    public abstract IEnumerable<WindowNode> DescendantWindows();
}

public sealed class WindowNode(long windowId, string title) : LayoutNode
{
    public long WindowId { get; } = windowId;
    public string Title { get; set; } = title;
    public long? OwnerWindowId { get; set; }
    public bool Urgent { get; set; }
    public override IEnumerable<WindowNode> DescendantWindows() { yield return this; }
}

public sealed class ContainerNode(LayoutKind layout) : LayoutNode
{
    public LayoutKind Layout { get; internal set; } = layout;
    public LayoutKind LastSplitLayout { get; internal set; } =
        layout == LayoutKind.SplitVertical ? LayoutKind.SplitVertical : LayoutKind.SplitHorizontal;
    public List<LayoutNode> Children { get; } = [];
    public long? ActiveChildId { get; internal set; }
    public LayoutNode? ActiveChild => Children.FirstOrDefault(child => child.Id == ActiveChildId) ?? Children.FirstOrDefault();

    public override IEnumerable<WindowNode> DescendantWindows() => Children.SelectMany(child => child.DescendantWindows());

    internal void Insert(int index, LayoutNode node)
    {
        node.Parent = this;
        Children.Insert(Math.Clamp(index, 0, Children.Count), node);
        ActiveChildId ??= node.Id;
    }
}

public sealed class WorkspaceState(string name, LayoutKind defaultLayout)
{
    public string Name { get; internal set; } = name;
    public string? OutputName { get; internal set; }
    public ContainerNode Root { get; } = new(defaultLayout);
    public List<LayoutNode> Floating { get; } = [];
    public long? LastFocusedWindowId { get; internal set; }
    public long? LastTilingWindowId { get; internal set; }
    public long? LastFloatingWindowId { get; internal set; }
    public LayoutNode? FullscreenNode { get; internal set; }
    public IEnumerable<WindowNode> AllWindows() => Root.DescendantWindows().Concat(Floating.SelectMany(node => node.DescendantWindows()));
}

public sealed class OutputState(string name, PixelRect bounds, PixelRect workArea, bool primary = false)
{
    public string Name { get; } = name;
    public PixelRect Bounds { get; internal set; } = bounds;
    public PixelRect WorkArea { get; internal set; } = workArea;
    public bool Primary { get; internal set; } = primary;
    public string ActiveWorkspace { get; internal set; } = "1";
    public string? PreviousWorkspace { get; internal set; }
}

public sealed record WindowLayout(long WindowId, long NodeId, PixelRect Bounds, bool Visible, bool Floating, bool Fullscreen, int BorderWidth);
public sealed record TabLabel(long NodeId, string Title, PixelRect Bounds, bool Active);
public sealed record ContainerDecoration(long NodeId, LayoutKind Layout, IReadOnlyList<TabLabel> Tabs);
public sealed record LayoutSnapshot(IReadOnlyList<WindowLayout> Windows, IReadOnlyList<ContainerDecoration> Decorations);

internal sealed class ScratchpadEntry(LayoutNode node)
{
    public LayoutNode Node { get; } = node;
    public string? ShownWorkspace { get; set; }
}

using SwayWin.Core.Configuration;
using SwayWin.Core.Layout;

namespace SwayWin.Tests;

public static class LayoutTests
{
    private static readonly PixelRect Area = new(0, 0, 1920, 1000);

    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("horizontal split / six-pixel gaps / no lost pixels", Horizontal);
        yield return ("smart gaps and borders", SmartGaps);
        yield return ("nested vertical split and hierarchical focus", Nested);
        yield return ("tabs and stacking show only the active child", Tabs);
        yield return ("layout toggle split does not cycle tabs", ToggleSplit);
        yield return ("focus parent and move a nested container", MoveParent);
        yield return ("workspace auto back-and-forth and move without following", Workspaces);
        yield return ("multiple outputs keep independent visible workspaces", MultipleOutputs);
        yield return ("floating toggle and mode focus", Floating);
        yield return ("scratchpad hides, shows, cycles and follows workspace", Scratchpad);
        yield return ("nested scratchpad container preserves its tree", ScratchpadContainer);
        yield return ("fullscreen uses monitor bounds, not work area", Fullscreen);
        yield return ("pixel-step tiled and floating resize", Resize);
        yield return ("directional movement and parent links", Move);
        yield return ("destroyed windows leave no stale focus", Remove);
        yield return ("tab and stack focus wrap preserves child order", FocusWrapping);
        yield return ("output work-area updates preserve focused parent", OutputUpdateFocus);
        yield return ("rename and workspace-output movement", Rename);
        yield return ("deterministic randomized layout invariants", Randomized);
    }

    private static WindowManagerState Create(WindowSettings? settings = null) => new(settings ?? new()
    { GapInner = 6, GapOuter = 0, HideEdgeBorders = "smart", SmartGaps = "on", MouseWarping = false, WorkspaceAutoBackAndForth = true },
        [new OutputState("display1", new(0, 0, 1920, 1080), Area, true)]);

    private static WindowNode Add(WindowManagerState state, long id, bool floating = false) => state.AddWindow(id, $"Window {id}", new(100, 100, 640, 480), floating);
    private static WindowLayout At(WindowManagerState state, long id) => state.CalculateLayout().Windows.Single(window => window.WindowId == id);

    private static void Horizontal()
    {
        WindowManagerState state = Create();
        Add(state, 1); Add(state, 2);
        Equal(new PixelRect(0, 0, 957, 1000), At(state, 1).Bounds);
        Equal(new PixelRect(963, 0, 957, 1000), At(state, 2).Bounds);
        Equal(2, At(state, 1).BorderWidth);
        Add(state, 3);
        Equal(1920, state.CalculateLayout().Windows.Sum(window => window.Bounds.Width) + 12);
    }

    private static void SmartGaps()
    {
        WindowManagerState state = Create(new() { GapInner = 6, GapOuter = 8, SmartGaps = "on", HideEdgeBorders = "smart" });
        Add(state, 1);
        Equal(Area, At(state, 1).Bounds);
        Equal(0, At(state, 1).BorderWidth);
        Add(state, 2);
        Equal(8, At(state, 1).Bounds.X);
        Equal(8, At(state, 1).Bounds.Y);
        Equal(2, At(state, 1).BorderWidth);
        state.SetLayout("tabbed");
        Equal(1, state.CalculateLayout().Windows.Count(window => window.Visible));
        Equal(0, At(state, 2).BorderWidth);
    }

    private static void Nested()
    {
        WindowManagerState state = Create();
        Add(state, 1); WindowNode second = Add(state, 2);
        state.Split(true); WindowNode third = Add(state, 3);
        Equal(LayoutKind.SplitHorizontal, state.CurrentWorkspace.Root.Layout);
        Assert(second.Parent == third.Parent && second.Parent != state.CurrentWorkspace.Root, "Missing nested container.");
        Equal(LayoutKind.SplitVertical, third.Parent!.Layout);
        Equal(new PixelRect(963, 0, 957, 497), At(state, 2).Bounds);
        Equal(new PixelRect(963, 503, 957, 497), At(state, 3).Bounds);
        state.FocusDirection(Direction.Up); Equal(2L, state.FocusedWindow!.WindowId);
        state.FocusDirection(Direction.Left); Equal(1L, state.FocusedWindow!.WindowId);
        state.FocusDirection(Direction.Right); Equal(2L, state.FocusedWindow!.WindowId);
    }

    private static void Tabs()
    {
        WindowManagerState state = Create();
        Add(state, 1); Add(state, 2); Add(state, 3);
        state.SetLayout("tabbed");
        LayoutSnapshot snapshot = state.CalculateLayout();
        Equal(1, snapshot.Windows.Count(window => window.Visible));
        Equal(3, snapshot.Decorations.Single().Tabs.Count);
        Equal(3L, snapshot.Windows.Single(window => window.Visible).WindowId);
        state.FocusDirection(Direction.Left);
        Equal(2L, state.FocusedWindow!.WindowId);
        Equal(2L, state.CalculateLayout().Windows.Single(window => window.Visible).WindowId);
        state.SetLayout("stacking");
        snapshot = state.CalculateLayout();
        Equal(72, snapshot.Windows.Single(window => window.Visible).Bounds.Y);
        state.FocusDirection(Direction.Up); Equal(1L, state.FocusedWindow!.WindowId);
    }

    private static void ToggleSplit()
    {
        WindowManagerState state = Create();
        Add(state, 1); Add(state, 2);
        state.SetLayout("toggle split"); Equal(LayoutKind.SplitVertical, state.CurrentWorkspace.Root.Layout);
        state.SetLayout("tabbed");
        state.SetLayout("toggle split"); Equal(LayoutKind.SplitVertical, state.CurrentWorkspace.Root.Layout);
        state.SetLayout("toggle split"); Equal(LayoutKind.SplitHorizontal, state.CurrentWorkspace.Root.Layout);
    }

    private static void MoveParent()
    {
        WindowManagerState state = Create();
        Add(state, 1); Add(state, 2); state.Split(true); Add(state, 3);
        state.FocusParent();
        ContainerNode parent = (ContainerNode)state.SelectedNode!;
        Equal(2, parent.DescendantWindows().Count());
        state.MoveFocusedToWorkspace("2");
        Equal("1", state.CurrentWorkspace.Name);
        Equal(1L, state.FocusedWindow!.WindowId);
        Equal(2, state.GetWorkspace("2").AllWindows().Count());
        Assert(state.GetWorkspace("2").Root.Children.Contains(parent), "Nested group flattened during workspace move.");
        Equal(1, state.CalculateLayout().Windows.Count(window => window.Visible));
    }

    private static void Workspaces()
    {
        WindowManagerState state = Create();
        Add(state, 1); Add(state, 2);
        state.MoveFocusedToWorkspace("2");
        Equal("1", state.CurrentWorkspace.Name);
        Equal(1L, state.FocusedWindow!.WindowId);
        Equal(false, At(state, 2).Visible);
        state.SwitchWorkspace("2"); Equal(2L, state.FocusedWindow!.WindowId);
        state.SwitchWorkspace("2"); Equal("1", state.CurrentWorkspace.Name);
        state.BackAndForth(); Equal("2", state.CurrentWorkspace.Name);
        state.SwitchWorkspace("2", true); Equal("2", state.CurrentWorkspace.Name);
    }

    private static void MultipleOutputs()
    {
        WindowManagerState state = Create();
        Add(state, 1);
        state.SetOutputs([new("display1", new(0, 0, 1920, 1080), Area, true), new("display2", new(-1600, 0, 1600, 900), new(-1600, 0, 1600, 860))]);
        state.FocusOutput("display2"); Add(state, 2);
        Equal(2, state.CalculateLayout().Windows.Count(window => window.Visible));
        Equal(-1600, At(state, 2).Bounds.X);
        state.SwitchWorkspace("1"); Equal("display1", state.ActiveOutput.Name);
        state.FocusOutput(Direction.Left); Equal("display2", state.ActiveOutput.Name);
        state.SwitchWorkspace("3"); Equal("display2", state.ActiveOutput.Name);
        state.SetOutputs([new("display1", new(0, 0, 1920, 1080), Area, true)]);
        Equal(2, state.Windows.Count);
        Equal("display1", state.ActiveOutput.Name);
    }

    private static void Floating()
    {
        WindowManagerState state = Create();
        Add(state, 1); WindowNode window = Add(state, 2);
        state.ToggleFloating();
        Equal(true, window.IsFloating);
        Equal(1, state.CurrentWorkspace.Floating.Count);
        Equal(new PixelRect(100, 100, 640, 480), state.GetFloatingBounds(2));
        state.FocusMode("mode_toggle"); Equal(1L, state.FocusedWindow!.WindowId);
        state.FocusMode("mode_toggle"); Equal(2L, state.FocusedWindow!.WindowId);
        state.ToggleFloating(); Equal(false, window.IsFloating);
        Equal(2, state.CurrentWorkspace.Root.DescendantWindows().Count());
        Equal(0, state.CurrentWorkspace.Floating.Count);
        state.ToggleFloating(); state.SetFloatingBounds(2, new(200, 300, 500, 400));
        state.ToggleFloating(); state.ToggleFloating();
        Equal(new PixelRect(200, 300, 500, 400), state.GetFloatingBounds(2));
    }

    private static void Scratchpad()
    {
        WindowManagerState state = Create();
        Add(state, 1); state.MoveToScratchpad();
        Add(state, 2); state.MoveToScratchpad();
        Equal(2, state.ScratchpadCount);
        Equal(0, state.CalculateLayout().Windows.Count(window => window.Visible));
        state.ShowScratchpad(); Equal(1L, state.FocusedWindow!.WindowId);
        Equal(true, At(state, 1).Floating);
        state.ShowScratchpad(); Equal(0, state.CalculateLayout().Windows.Count(window => window.Visible));
        state.ShowScratchpad(); Equal(2L, state.FocusedWindow!.WindowId);
        state.ShowScratchpad();
        state.SwitchWorkspace("2"); state.ShowScratchpad();
        Equal("2", state.WorkspaceOf(state.FocusedWindow!)!.Name);
        Equal(1, state.CalculateLayout().Windows.Count(window => window.Visible));
    }

    private static void ScratchpadContainer()
    {
        WindowManagerState state = Create();
        Add(state, 1); Add(state, 2); state.Split(true); Add(state, 3);
        state.FocusParent(); state.MoveToScratchpad();
        Equal(1, state.ScratchpadCount);
        Equal(1, state.CalculateLayout().Windows.Count(window => window.Visible));
        state.ShowScratchpad();
        Equal(3, state.CalculateLayout().Windows.Count(window => window.Visible));
        Equal(true, At(state, 2).Floating);
        Assert(At(state, 2).Bounds.Bottom <= At(state, 3).Bounds.Y, "Floating group lost its vertical split.");
        state.ShowScratchpad(); Equal(1, state.CalculateLayout().Windows.Count(window => window.Visible));
    }

    private static void Fullscreen()
    {
        WindowManagerState state = Create();
        Add(state, 1); Add(state, 2); state.ToggleFullscreen();
        WindowLayout full = state.CalculateLayout().Windows.Single(window => window.Visible);
        Equal(2L, full.WindowId); Equal(new PixelRect(0, 0, 1920, 1080), full.Bounds); Equal(0, full.BorderWidth);
        state.ToggleFullscreen(); Equal(2, state.CalculateLayout().Windows.Count(window => window.Visible));
    }

    private static void Resize()
    {
        WindowManagerState state = Create();
        Add(state, 1); Add(state, 2);
        int before = At(state, 2).Bounds.Width;
        state.Resize(ResizeAxis.Width, 20);
        Equal(before + 20, At(state, 2).Bounds.Width);
        state.Resize(ResizeAxis.Width, -20); Equal(before, At(state, 2).Bounds.Width);
        state.ToggleFloating();
        PixelRect bounds = state.GetFloatingBounds(2);
        state.Resize(ResizeAxis.Height, 20);
        Equal(bounds.Height + 20, state.GetFloatingBounds(2).Height);
        state.Resize(ResizeAxis.Width, -100000);
        Equal(state.Settings.MinimumFloatingWidth, state.GetFloatingBounds(2).Width);
    }

    private static void Move()
    {
        WindowManagerState state = Create();
        WindowNode first = Add(state, 1); WindowNode second = Add(state, 2);
        state.MoveDirection(Direction.Left);
        Equal(second, state.CurrentWorkspace.Root.Children[0]);
        Equal(first, state.CurrentWorkspace.Root.Children[1]);
        state.Split(true); WindowNode third = Add(state, 3);
        state.MoveDirection(Direction.Right);
        Assert(third.Parent is not null, "Moved tile orphaned.");
        Equal(3, state.CurrentWorkspace.Root.DescendantWindows().Count());
        state.FocusWindow(1); state.ToggleFloating();
        int x = state.GetFloatingBounds(1).X;
        state.MoveDirection(Direction.Right); Equal(x + 10, state.GetFloatingBounds(1).X);
    }

    private static void Remove()
    {
        WindowManagerState state = Create();
        Add(state, 1); Add(state, 2); state.SetLayout("tabbed");
        state.RemoveWindow(2); Equal(1L, state.FocusedWindow!.WindowId);
        Equal(1, state.CalculateLayout().Windows.Count(window => window.Visible));
        state.RemoveWindow(1); Equal(0, state.Windows.Count);
        Assert(state.FocusedWindow is null, "Stale focus remains after destroying all windows.");
        Add(state, 3); state.MoveToScratchpad(); state.RemoveWindow(3); Equal(0, state.ScratchpadCount);
    }

    private static void Rename()
    {
        WindowManagerState state = Create(); Add(state, 1);
        state.RenameWorkspace("1:code"); Equal("1:code", state.CurrentWorkspace.Name);
        state.SetOutputs([new("display1", new(0, 0, 1920, 1080), Area, true), new("display2", new(1920, 0, 1600, 900), new(1920, 0, 1600, 860))]);
        state.MoveWorkspaceToOutput("display2");
        Equal("display2", state.ActiveOutput.Name); Equal("1:code", state.CurrentWorkspace.Name);
        Equal(1920, At(state, 1).Bounds.X);
    }

    private static void FocusWrapping()
    {
        WindowManagerState state = Create(); Add(state, 1); Add(state, 2); Add(state, 3);
        state.SetLayout("tabbed"); state.FocusDirection(Direction.Right); Equal(1L, state.FocusedWindow!.WindowId);
        state.SetLayout("stacking"); state.FocusDirection(Direction.Up); Equal(3L, state.FocusedWindow!.WindowId);
    }

    private static void OutputUpdateFocus()
    {
        WindowManagerState state = Create(); Add(state, 1); Add(state, 2); state.Split(true); Add(state, 3);
        state.FocusParent(); LayoutNode selected = state.SelectedNode!;
        state.SetOutputs([new("display1", new(0, 0, 1920, 1080), new(0, 24, 1920, 976), true)]);
        Equal(selected, state.SelectedNode);
    }

    private static void Randomized()
    {
        foreach (int seed in new[] { 187, 3, 41, 97, 311, 809, 1103, 2026 }) RandomizedSeed(seed);
    }

    private static void RandomizedSeed(int seed)
    {
        WindowManagerState state = Create();
        Random random = new(seed);
        long next = 1;
        for (int i = 0; i < 700; i++)
        {
            long[] ids = state.Windows.Keys.ToArray();
            if (ids.Length > 0) state.FocusWindow(ids[random.Next(ids.Length)]);
            switch (random.Next(12))
            {
                case 0: Add(state, next++); break;
                case 1: if (ids.Length > 0) state.RemoveWindow(ids[random.Next(ids.Length)]); break;
                case 2: state.Split(random.Next(2) == 0); break;
                case 3: state.SetLayout(new[] { "tabbed", "stacking", "toggle split" }[random.Next(3)]); break;
                case 4: state.ToggleFloating(); break;
                case 5: state.SwitchWorkspace(random.Next(1, 5).ToString()); break;
                case 6: state.MoveFocusedToWorkspace(random.Next(1, 5).ToString()); break;
                case 7: state.MoveToScratchpad(); break;
                case 8: state.ShowScratchpad(); break;
                case 9: state.Resize((ResizeAxis)random.Next(2), random.Next(-10, 11) * 20); break;
                case 10: state.FocusParent(); state.MoveDirection((Direction)random.Next(4)); break;
                case 11: state.FocusDirection((Direction)random.Next(4)); break;
            }
            LayoutSnapshot snapshot = state.CalculateLayout();
            Equal(state.Windows.Count, snapshot.Windows.Count, $"Snapshot count at step {i}");
            Equal(state.Windows.Count, snapshot.Windows.Select(window => window.WindowId).Distinct().Count(), $"Duplicate layout at step {i}");
            Assert(snapshot.Windows.All(window => state.Windows.ContainsKey(window.WindowId)), $"Orphan layout at step {i}");
            foreach (WorkspaceState workspace in state.Workspaces.Values) CheckParents(workspace.Root);
        }
    }

    private static void CheckParents(ContainerNode container)
    {
        foreach (LayoutNode node in container.Children)
        {
            Assert(node.Parent == container, "Stale parent link.");
            Assert(node.Weight > 0 && double.IsFinite(node.Weight), "Invalid split weight.");
            if (node is ContainerNode child) CheckParents(child);
        }
    }

    public static void Equal<T>(T expected, T actual, string? message = null)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual)) throw new InvalidOperationException($"{message}: expected {expected}, actual {actual}");
    }

    public static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }
}

using SwayWin.Core.Configuration;

namespace SwayWin.Core.Layout;

public sealed class WindowManagerState
{
    private readonly List<ScratchpadEntry> scratchpad = [];
    private int scratchpadIndex;
    private string? activeOutputName;
    private LayoutNode? selected;

    public WindowManagerState(WindowSettings settings, IEnumerable<OutputState> outputs)
    {
        Settings = settings;
        SetOutputs(outputs);
    }

    public WindowSettings Settings { get; set; }
    public Dictionary<string, WorkspaceState> Workspaces { get; } = new(StringComparer.Ordinal);
    public Dictionary<long, WindowNode> Windows { get; } = [];
    public List<OutputState> Outputs { get; } = [];
    public OutputState ActiveOutput => Outputs.First(output => output.Name == activeOutputName);
    public WorkspaceState CurrentWorkspace => GetWorkspace(ActiveOutput.ActiveWorkspace);
    public LayoutNode? SelectedNode => selected;
    public WindowNode? FocusedWindow { get; private set; }
    public int ScratchpadCount => scratchpad.Count;
    public int HeaderHeight { get; set; } = 24;

    public WorkspaceState GetWorkspace(string name)
    {
        if (!Workspaces.TryGetValue(name, out WorkspaceState? workspace))
        {
            LayoutKind layout = Settings.WorkspaceLayout.ToLowerInvariant() switch
            {
                "splitv" => LayoutKind.SplitVertical,
                "tabbed" => LayoutKind.Tabbed,
                "stacking" => LayoutKind.Stacked,
                _ => LayoutKind.SplitHorizontal
            };
            workspace = new(name, layout);
            Workspaces.Add(name, workspace);
        }
        return workspace;
    }

    public void SetOutputs(IEnumerable<OutputState> available)
    {
        LayoutNode? previousSelection = selected;
        long? previousWindow = FocusedWindow?.WindowId;
        OutputState[] incoming = available.ToArray();
        if (incoming.Length == 0) throw new ArgumentException("At least one output is required.", nameof(available));
        Dictionary<string, OutputState> old = Outputs.ToDictionary(output => output.Name, StringComparer.Ordinal);
        Outputs.Clear();
        foreach (OutputState item in incoming.OrderByDescending(output => output.Primary))
        {
            if (old.TryGetValue(item.Name, out OutputState? existing))
            {
                existing.Bounds = item.Bounds;
                existing.WorkArea = item.WorkArea;
                existing.Primary = item.Primary;
                Outputs.Add(existing);
            }
            else
            {
                int number = 1;
                while (Outputs.Any(output => output.ActiveWorkspace == number.ToString())) number++;
                item.ActiveWorkspace = number.ToString();
                Outputs.Add(item);
            }
        }
        activeOutputName = Outputs.Any(output => output.Name == activeOutputName) ? activeOutputName : Outputs[0].Name;
        foreach (WorkspaceState workspace in Workspaces.Values)
            if (workspace.OutputName is not null && Outputs.All(output => output.Name != workspace.OutputName))
                workspace.OutputName = Outputs[0].Name;
        foreach (OutputState output in Outputs) GetWorkspace(output.ActiveWorkspace).OutputName = output.Name;
        if (previousSelection is not null && WorkspaceOf(previousSelection) == CurrentWorkspace)
            FocusNode(previousSelection);
        else if (previousWindow is long windowId && Windows.TryGetValue(windowId, out WindowNode? window) && WorkspaceOf(window) == CurrentWorkspace)
            FocusNode(window);
        else SelectWorkspaceFocus(CurrentWorkspace);
    }

    public WorkspaceState? WorkspaceOf(WindowNode window) => Workspaces.Values.FirstOrDefault(workspace => workspace.AllWindows().Contains(window));
    public WorkspaceState? WorkspaceOf(LayoutNode node) => Workspaces.Values.FirstOrDefault(workspace =>
        ReferenceEquals(workspace.Root, node) || ContainsNode(workspace.Root, node) || workspace.Floating.Any(root => ContainsNode(root, node)));

    public WindowNode AddWindow(long windowId, string title, PixelRect initialBounds, bool floating = false,
        string? workspaceName = null, bool focus = true, long? ownerWindowId = null)
    {
        if (Windows.TryGetValue(windowId, out WindowNode? existing)) return existing;
        WorkspaceState workspace = workspaceName is null ? CurrentWorkspace : GetWorkspace(workspaceName);
        workspace.OutputName ??= ActiveOutput.Name;
        WindowNode node = new(windowId, title) { FloatingBounds = initialBounds, OwnerWindowId = ownerWindowId };
        Windows.Add(windowId, node);
        if (floating)
        {
            MarkFloating(node, true);
            node.FloatingBounds = initialBounds.FitInside(OutputFor(workspace).WorkArea);
            workspace.Floating.Add(node);
        }
        else
        {
            ContainerNode parent = InsertionParent(workspace);
            LayoutNode? sibling = parent.Children.FirstOrDefault(child => selected is not null && ContainsNode(child, selected));
            int index = sibling is null ? parent.Children.Count : parent.Children.IndexOf(sibling) + 1;
            node.Weight = parent.Children.Count == 0 ? 1 : parent.Children.Average(child => child.Weight);
            parent.Insert(index, node);
        }
        if (focus && workspace == CurrentWorkspace && workspace.FullscreenNode is null) FocusNode(node);
        else if (workspace.LastFocusedWindowId is null) workspace.LastFocusedWindowId = windowId;
        return node;
    }

    public void RemoveWindow(long windowId)
    {
        if (!Windows.Remove(windowId, out WindowNode? window)) return;
        WorkspaceState? workspace = WorkspaceOf(window);
        bool wasFocused = FocusedWindow == window;
        ScratchpadEntry? entry = scratchpad.FirstOrDefault(item => ContainsNode(item.Node, window));
        if (entry is not null && entry.Node == window)
        {
            scratchpad.Remove(entry);
            if (entry.ShownWorkspace is not null) GetWorkspace(entry.ShownWorkspace).Floating.Remove(entry.Node);
        }
        else if (window.Parent is not null)
        {
            ContainerNode parent = window.Parent;
            parent.Children.Remove(window);
            window.Parent = null;
            Normalize(parent, workspace);
            if (entry is not null && !entry.Node.DescendantWindows().Any()) scratchpad.Remove(entry);
        }
        else workspace?.Floating.Remove(window);
        if (workspace?.FullscreenNode is not null && !workspace.FullscreenNode.DescendantWindows().Any(leaf => Windows.ContainsKey(leaf.WindowId)))
            workspace.FullscreenNode = null;
        if (wasFocused) SelectWorkspaceFocus(CurrentWorkspace);
        if (selected == window) selected = FocusedWindow;
    }

    public bool FocusWindow(long windowId, bool switchWorkspace = true)
    {
        if (!Windows.TryGetValue(windowId, out WindowNode? window)) return false;
        WorkspaceState? workspace = WorkspaceOf(window);
        if (workspace is null) return false;
        if (workspace != CurrentWorkspace)
        {
            if (!switchWorkspace) return false;
            SwitchWorkspace(workspace.Name, noAutoBackAndForth: true);
        }
        if (workspace.FullscreenNode is not null && !ContainsNode(workspace.FullscreenNode, window) && !window.IsFloating) return false;
        FocusNode(window);
        return true;
    }

    public bool FocusNodeById(long id)
    {
        LayoutNode? node = FindNode(id);
        if (node is null) return false;
        WorkspaceState? workspace = WorkspaceOf(node);
        if (workspace is null) return false;
        if (workspace != CurrentWorkspace) SwitchWorkspace(workspace.Name, true);
        FocusNode(node);
        return true;
    }

    public void FocusParent()
    {
        if (selected?.Parent is { } parent) FocusNode(parent);
    }

    public void FocusChild()
    {
        if (selected is ContainerNode { ActiveChild: { } child }) FocusNode(child);
    }

    public void FocusMode(string mode)
    {
        WorkspaceState workspace = CurrentWorkspace;
        bool floating = mode == "floating" || (mode == "mode_toggle" && FocusedWindow?.IsFloating != true);
        IEnumerable<WindowNode> candidates = floating ? workspace.Floating.SelectMany(node => node.DescendantWindows()) : workspace.Root.DescendantWindows();
        long? remembered = floating ? workspace.LastFloatingWindowId : workspace.LastTilingWindowId;
        WindowNode? candidate = candidates.FirstOrDefault(node => node.WindowId == remembered) ?? candidates.LastOrDefault();
        if (candidate is not null) FocusNode(candidate);
    }

    public void FocusDirection(Direction direction)
    {
        CalculateLayout();
        LayoutNode? origin = selected;
        LayoutNode? wrapTarget = null;
        if (origin is null) { SelectWorkspaceFocus(CurrentWorkspace); return; }
        if (origin.IsFloating)
        {
            WindowNode? next = NearestWindow(CurrentWorkspace.Floating.SelectMany(node => node.DescendantWindows()), origin, direction);
            if (next is not null) { FocusNode(next); return; }
        }
        else
        {
            for (LayoutNode branch = origin; branch.Parent is { } parent; branch = parent)
            {
                if (!Navigates(parent.Layout, direction)) continue;
                int index = parent.Children.IndexOf(branch) + DirectionStep(direction);
                if (index < 0 || index >= parent.Children.Count)
                {
                    wrapTarget = parent.Children[DirectionStep(direction) > 0 ? 0 : parent.Children.Count - 1];
                    continue;
                }
                FocusNode(Descend(parent.Children[index], direction, origin.Bounds));
                return;
            }
        }
        OutputState? output = NearestOutput(direction);
        if (output is not null) { FocusOutput(output.Name); return; }
        if (!origin.IsFloating)
        {
            if (wrapTarget is not null) { FocusNode(Descend(wrapTarget, direction, origin.Bounds)); return; }
            // Sway's default focus wrapping applies after exhausting the container hierarchy.
            IEnumerable<WindowNode> windows = CurrentWorkspace.Root.DescendantWindows();
            WindowNode? wrapped = EdgeWindow(windows, direction, origin.Bounds);
            if (wrapped is not null) FocusNode(wrapped);
        }
    }

    public void FocusSibling(bool next)
    {
        if (selected?.Parent is not { } parent || parent.Children.Count == 0) return;
        int index = (parent.Children.IndexOf(selected) + (next ? 1 : parent.Children.Count - 1)) % parent.Children.Count;
        FocusNode(ActiveLeaf(parent.Children[index]) ?? parent.Children[index]);
    }

    public void Split(bool vertical)
    {
        LayoutKind layout = vertical ? LayoutKind.SplitVertical : LayoutKind.SplitHorizontal;
        LayoutNode? node = selected;
        if (node is null || node == CurrentWorkspace.Root) { ChangeLayout(CurrentWorkspace.Root, layout); return; }
        if (node.IsFloating) return;
        ContainerNode? parent = node.Parent;
        if (parent is null) return;
        if (parent.Children.Count == 1 || parent.Layout == layout) { ChangeLayout(parent, layout); return; }
        int index = parent.Children.IndexOf(node);
        ContainerNode wrapper = new(layout) { Weight = node.Weight };
        parent.Children[index] = wrapper;
        wrapper.Parent = parent;
        node.Weight = 1;
        wrapper.Insert(0, node);
        parent.ActiveChildId = wrapper.Id;
    }

    public void SetLayout(string command)
    {
        ContainerNode container = selected as ContainerNode ?? selected?.Parent ?? CurrentWorkspace.Root;
        LayoutKind? layout = command.ToLowerInvariant().Trim() switch
        {
            "splith" or "split horizontal" => LayoutKind.SplitHorizontal,
            "splitv" or "split vertical" => LayoutKind.SplitVertical,
            "tabbed" => LayoutKind.Tabbed,
            "stacking" or "stacked" => LayoutKind.Stacked,
            "toggle split" => container.Layout switch
            {
                LayoutKind.SplitHorizontal => LayoutKind.SplitVertical,
                LayoutKind.SplitVertical => LayoutKind.SplitHorizontal,
                _ => container.LastSplitLayout
            },
            _ => null
        };
        if (layout is not null) ChangeLayout(container, layout.Value);
    }

    public void MoveDirection(Direction direction, int pixels = 10)
    {
        LayoutNode? node = MovableSelection();
        if (node is null) return;
        if (node.IsFloating)
        {
            LayoutNode root = FloatingRoot(node);
            PixelRect rectangle = root.FloatingBounds;
            root.FloatingBounds = rectangle with
            {
                X = rectangle.X + (direction == Direction.Left ? -pixels : direction == Direction.Right ? pixels : 0),
                Y = rectangle.Y + (direction == Direction.Up ? -pixels : direction == Direction.Down ? pixels : 0)
            };
            return;
        }
        WorkspaceState workspace = CurrentWorkspace;
        for (LayoutNode branch = node; branch.Parent is { } parent; branch = parent)
        {
            if (!NavigatesSplit(parent.Layout, direction)) continue;
            int index = parent.Children.IndexOf(branch);
            int neighborIndex = index + DirectionStep(direction);
            if (neighborIndex < 0 || neighborIndex >= parent.Children.Count) continue;
            LayoutNode neighbor = parent.Children[neighborIndex];
            if (branch == node && neighbor is not ContainerNode)
            {
                (parent.Children[index], parent.Children[neighborIndex]) = (parent.Children[neighborIndex], parent.Children[index]);
            }
            else
            {
                ContainerNode oldParent = node.Parent!;
                oldParent.Children.Remove(node);
                node.Parent = null;
                if (neighbor is ContainerNode destination)
                {
                    int at = NavigatesSplit(destination.Layout, direction)
                        ? (DirectionStep(direction) > 0 ? 0 : destination.Children.Count)
                        : destination.Children.FindIndex(child => child.Id == destination.ActiveChildId) + 1;
                    node.Weight = 1;
                    destination.Insert(at, node);
                }
                else
                {
                    int at = parent.Children.IndexOf(branch) + (DirectionStep(direction) > 0 ? 1 : 0);
                    node.Weight = 1;
                    parent.Insert(at, node);
                }
                Normalize(oldParent, workspace);
            }
            FocusNode(node);
            return;
        }
        ContainerNode rootContainer = workspace.Root;
        if (rootContainer.DescendantWindows().Count() <= node.DescendantWindows().Count()) return;
        ContainerNode previous = new(rootContainer.Layout);
        foreach (LayoutNode child in rootContainer.Children.ToArray()) previous.Insert(previous.Children.Count, child);
        rootContainer.Children.Clear();
        rootContainer.Insert(0, previous);
        ChangeLayout(rootContainer, IsHorizontal(direction) ? LayoutKind.SplitHorizontal : LayoutKind.SplitVertical);
        ContainerNode source = node.Parent!;
        source.Children.Remove(node);
        node.Parent = null;
        rootContainer.Insert(DirectionStep(direction) > 0 ? rootContainer.Children.Count : 0, node);
        Normalize(source, workspace);
        FocusNode(node);
    }

    public void ToggleFloating(string action = "toggle")
    {
        LayoutNode? node = MovableSelection();
        if (node is null) return;
        bool enable = action == "enable" || (action == "toggle" && !node.IsFloating);
        if (enable == node.IsFloating) return;
        WorkspaceState workspace = CurrentWorkspace;
        CalculateLayout();
        if (enable)
        {
            PixelRect old = node.Bounds;
            Detach(node, workspace);
            MarkFloating(node, true);
            PixelRect area = OutputFor(workspace).WorkArea;
            PixelRect remembered = node.FloatingBounds;
            bool hasGeometry = remembered.Width > 0 && remembered.Height > 0;
            int width = Math.Max(Settings.MinimumFloatingWidth, hasGeometry ? remembered.Width : old.Width);
            int height = Math.Max(Settings.MinimumFloatingHeight, hasGeometry ? remembered.Height : old.Height);
            if (Settings.MaximumFloatingWidth > 0) width = Math.Min(width, Settings.MaximumFloatingWidth);
            if (Settings.MaximumFloatingHeight > 0) height = Math.Min(height, Settings.MaximumFloatingHeight);
            node.FloatingBounds = new PixelRect(hasGeometry ? remembered.X : area.CenterX - width / 2,
                hasGeometry ? remembered.Y : area.CenterY - height / 2, width, height).FitInside(area);
            workspace.Floating.Add(node);
        }
        else
        {
            LayoutNode root = FloatingRoot(node);
            workspace.Floating.Remove(root);
            ScratchpadEntry? entry = scratchpad.FirstOrDefault(item => item.Node == root);
            if (entry is not null) scratchpad.Remove(entry);
            MarkFloating(root, false);
            MarkScratchpad(root, false);
            root.Weight = 1;
            workspace.Root.Insert(workspace.Root.Children.Count, root);
            node = root;
        }
        FocusNode(node);
    }

    public void ToggleFullscreen(string action = "toggle")
    {
        LayoutNode? node = MovableSelection();
        if (node is null) return;
        WorkspaceState workspace = CurrentWorkspace;
        bool enabled = workspace.FullscreenNode is not null && ContainsNode(workspace.FullscreenNode, node);
        workspace.FullscreenNode = action == "disable" || (action == "toggle" && enabled) ? null : node;
    }

    public void SwitchWorkspace(string name, bool noAutoBackAndForth = false, string? preferredOutput = null)
    {
        if (!noAutoBackAndForth && Settings.WorkspaceAutoBackAndForth && ActiveOutput.ActiveWorkspace == name && ActiveOutput.PreviousWorkspace is not null)
            name = ActiveOutput.PreviousWorkspace;
        WorkspaceState workspace = GetWorkspace(name);
        OutputState? visibleOutput = Outputs.FirstOrDefault(output => output.ActiveWorkspace == name);
        OutputState output = visibleOutput ?? Outputs.FirstOrDefault(item => item.Name == preferredOutput)
            ?? Outputs.FirstOrDefault(item => item.Name == workspace.OutputName) ?? ActiveOutput;
        if (output.ActiveWorkspace != name)
        {
            WorkspaceState old = GetWorkspace(output.ActiveWorkspace);
            output.PreviousWorkspace = output.ActiveWorkspace;
            output.ActiveWorkspace = name;
            if (!old.AllWindows().Any()) old.OutputName = null;
        }
        workspace.OutputName = output.Name;
        activeOutputName = output.Name;
        SelectWorkspaceFocus(workspace);
    }

    public void BackAndForth()
    {
        if (ActiveOutput.PreviousWorkspace is { } previous) SwitchWorkspace(previous, true);
    }

    public void NextWorkspace(bool next)
    {
        string[] names = Workspaces.Keys.OrderBy(WorkspaceNumber).ThenBy(name => name, StringComparer.Ordinal).ToArray();
        if (names.Length == 0) return;
        int current = Array.IndexOf(names, CurrentWorkspace.Name);
        SwitchWorkspace(names[(current + (next ? 1 : names.Length - 1)) % names.Length], true);
    }

    public void FocusOutput(string name)
    {
        OutputState? output = Outputs.FirstOrDefault(item => item.Name == name);
        if (output is null) return;
        activeOutputName = output.Name;
        SelectWorkspaceFocus(CurrentWorkspace);
    }

    public void FocusOutput(Direction direction)
    {
        OutputState? output = NearestOutput(direction);
        if (output is not null) FocusOutput(output.Name);
    }

    public void MoveFocusedToWorkspace(string name)
    {
        LayoutNode? node = MovableSelection();
        if (node is null || name == CurrentWorkspace.Name) return;
        WorkspaceState source = CurrentWorkspace;
        WorkspaceState target = GetWorkspace(name);
        target.OutputName ??= source.OutputName;
        bool floating = node.IsFloating;
        if (floating) node = FloatingRoot(node);
        bool fullscreen = source.FullscreenNode is not null && ContainsNode(node, source.FullscreenNode);
        Detach(node, source);
        if (floating) target.Floating.Add(node);
        else { node.Weight = 1; target.Root.Insert(target.Root.Children.Count, node); }
        if (fullscreen) { source.FullscreenNode = null; target.FullscreenNode = node; }
        ScratchpadEntry? scratch = scratchpad.FirstOrDefault(entry => entry.Node == node);
        if (scratch is not null) scratch.ShownWorkspace = target.Name;
        target.LastFocusedWindowId = ActiveLeaf(node)?.WindowId;
        SelectWorkspaceFocus(source);
    }

    public void MoveWorkspaceToOutput(string outputName)
    {
        OutputState? target = Outputs.FirstOrDefault(output => output.Name == outputName);
        if (target is null || target == ActiveOutput) return;
        OutputState source = ActiveOutput;
        string name = source.ActiveWorkspace;
        string replacement = target.ActiveWorkspace;
        source.ActiveWorkspace = replacement;
        target.ActiveWorkspace = name;
        GetWorkspace(name).OutputName = target.Name;
        GetWorkspace(replacement).OutputName = source.Name;
        activeOutputName = target.Name;
        SelectWorkspaceFocus(CurrentWorkspace);
    }

    public void RenameWorkspace(string newName)
    {
        if (Workspaces.ContainsKey(newName)) throw new InvalidOperationException($"Workspace '{newName}' already exists.");
        WorkspaceState workspace = CurrentWorkspace;
        string oldName = workspace.Name;
        Workspaces.Remove(oldName);
        workspace.Name = newName;
        Workspaces.Add(newName, workspace);
        foreach (OutputState output in Outputs)
        {
            if (output.ActiveWorkspace == oldName) output.ActiveWorkspace = newName;
            if (output.PreviousWorkspace == oldName) output.PreviousWorkspace = newName;
        }
        foreach (ScratchpadEntry entry in scratchpad)
            if (entry.ShownWorkspace == oldName) entry.ShownWorkspace = newName;
    }

    public void MoveToScratchpad()
    {
        LayoutNode? node = MovableSelection();
        if (node is null) return;
        if (node.IsFloating) node = FloatingRoot(node);
        WorkspaceState workspace = CurrentWorkspace;
        ScratchpadEntry? existing = scratchpad.FirstOrDefault(entry => entry.Node == node);
        if (existing is not null)
        {
            workspace.Floating.Remove(node);
            existing.ShownWorkspace = null;
            SelectWorkspaceFocus(workspace);
            return;
        }
        CalculateLayout();
        bool floating = node.IsFloating;
        Detach(node, workspace);
        if (!floating)
        {
            PixelRect area = ActiveOutput.WorkArea;
            node.FloatingBounds = new(area.X + area.Width / 5, area.Y + area.Height / 5, area.Width * 3 / 5, area.Height * 3 / 5);
        }
        MarkFloating(node, true);
        MarkScratchpad(node, true);
        scratchpad.Add(new(node));
        SelectWorkspaceFocus(workspace);
    }

    public void ShowScratchpad(long? windowId = null)
    {
        if (scratchpad.Count == 0) return;
        WorkspaceState workspace = CurrentWorkspace;
        ScratchpadEntry? focused = scratchpad.FirstOrDefault(entry =>
            entry.ShownWorkspace == workspace.Name && FocusedWindow is not null && ContainsNode(entry.Node, FocusedWindow));
        if (focused is not null && (windowId is null || focused.Node.DescendantWindows().Any(window => window.WindowId == windowId)))
        {
            workspace.Floating.Remove(focused.Node);
            focused.ShownWorkspace = null;
            SelectWorkspaceFocus(workspace);
            return;
        }
        ScratchpadEntry? entry = windowId is not null
            ? scratchpad.FirstOrDefault(item => item.Node.DescendantWindows().Any(window => window.WindowId == windowId))
            : scratchpad[scratchpadIndex++ % scratchpad.Count];
        if (entry is null) return;
        if (entry.ShownWorkspace is not null) GetWorkspace(entry.ShownWorkspace).Floating.Remove(entry.Node);
        entry.ShownWorkspace = workspace.Name;
        entry.Node.FloatingBounds = entry.Node.FloatingBounds.FitInside(ActiveOutput.WorkArea);
        workspace.Floating.Add(entry.Node);
        FocusNode(entry.Node);
    }

    public void Resize(ResizeAxis axis, int amount, bool percentage = false)
    {
        LayoutNode? node = MovableSelection();
        if (node is null) return;
        CalculateLayout();
        if (node.IsFloating)
        {
            LayoutNode root = FloatingRoot(node);
            PixelRect old = root.FloatingBounds;
            int delta = percentage ? (axis == ResizeAxis.Width ? ActiveOutput.WorkArea.Width : ActiveOutput.WorkArea.Height) * amount / 100 : amount;
            int width = axis == ResizeAxis.Width ? Math.Max(Settings.MinimumFloatingWidth, old.Width + delta) : old.Width;
            int height = axis == ResizeAxis.Height ? Math.Max(Settings.MinimumFloatingHeight, old.Height + delta) : old.Height;
            if (Settings.MaximumFloatingWidth > 0) width = Math.Min(width, Settings.MaximumFloatingWidth);
            if (Settings.MaximumFloatingHeight > 0) height = Math.Min(height, Settings.MaximumFloatingHeight);
            root.FloatingBounds = new(old.X - (width - old.Width) / 2, old.Y - (height - old.Height) / 2, width, height);
            return;
        }
        for (LayoutNode branch = node; branch.Parent is { } parent; branch = parent)
        {
            if (parent.Layout != (axis == ResizeAxis.Width ? LayoutKind.SplitHorizontal : LayoutKind.SplitVertical) || parent.Children.Count < 2) continue;
            int index = parent.Children.IndexOf(branch);
            LayoutNode neighbor = parent.Children[index < parent.Children.Count - 1 ? index + 1 : index - 1];
            int size = axis == ResizeAxis.Width ? branch.Bounds.Width : branch.Bounds.Height;
            int otherSize = axis == ResizeAxis.Width ? neighbor.Bounds.Width : neighbor.Bounds.Height;
            int dimension = axis == ResizeAxis.Width ? parent.Bounds.Width : parent.Bounds.Height;
            int delta = percentage ? dimension * amount / 100 : amount;
            delta = Math.Clamp(delta, -Math.Max(0, size - 40), Math.Max(0, otherSize - 40));
            double pairWeight = branch.Weight + neighbor.Weight;
            branch.Weight = pairWeight * (size + delta) / Math.Max(1, size + otherSize);
            neighbor.Weight = pairWeight - branch.Weight;
            return;
        }
    }

    public void SetFloatingBounds(long windowId, PixelRect bounds)
    {
        if (Windows.TryGetValue(windowId, out WindowNode? window) && window.IsFloating)
            FloatingRoot(window).FloatingBounds = bounds with
            {
                Width = Math.Max(Settings.MinimumFloatingWidth, bounds.Width),
                Height = Math.Max(Settings.MinimumFloatingHeight, bounds.Height)
            };
    }

    public PixelRect GetFloatingBounds(long windowId) => Windows.TryGetValue(windowId, out WindowNode? window) ? FloatingRoot(window).FloatingBounds : default;

    public LayoutSnapshot CalculateLayout()
    {
        List<WindowLayout> windows = [];
        List<ContainerDecoration> decorations = [];
        foreach (WorkspaceState workspace in Workspaces.Values)
        {
            OutputState output = OutputFor(workspace);
            bool visible = output.ActiveWorkspace == workspace.Name;
            int visibleCount = VisibleLeafCount(workspace.Root);
            bool smart = Settings.SmartGaps is "on" or "yes" && visibleCount <= 1;
            bool inverse = Settings.SmartGaps == "inverse" && visibleCount > 1;
            int inner = smart || inverse ? 0 : Math.Max(0, Settings.GapInner);
            int outer = smart || inverse ? 0 : Math.Max(0, Settings.GapOuter);
            PixelRect rectangle = output.WorkArea.Inset(smart || inverse ? 0 : Math.Max(0, Settings.GapLeft ?? outer),
                smart || inverse ? 0 : Math.Max(0, Settings.GapTop ?? outer),
                smart || inverse ? 0 : Math.Max(0, Settings.GapRight ?? outer),
                smart || inverse ? 0 : Math.Max(0, Settings.GapBottom ?? outer));
            bool hideBorder = Settings.HideEdgeBorders == "smart" && visibleCount <= 1;
            LayoutNode? fullscreen = workspace.FullscreenNode;
            Render(workspace.Root, rectangle, visible && fullscreen is null, inner, hideBorder, false, windows, decorations);
            foreach (LayoutNode floating in workspace.Floating)
            {
                bool transient = fullscreen is not null && floating.DescendantWindows().Any(window =>
                    window.OwnerWindowId is long owner && fullscreen.DescendantWindows().Any(leaf => leaf.WindowId == owner));
                Render(floating, floating.FloatingBounds, visible && (fullscreen is null || transient), 0, false, false, windows, decorations);
            }
            if (fullscreen is not null)
            {
                int start = windows.Count;
                Render(fullscreen, output.Bounds, visible, 0, true, true, windows, decorations);
                HashSet<long> ids = windows.Skip(start).Select(item => item.WindowId).ToHashSet();
                windows.RemoveAll(item => ids.Contains(item.WindowId) && !item.Fullscreen);
            }
        }
        foreach (ScratchpadEntry entry in scratchpad.Where(item => item.ShownWorkspace is null))
            Render(entry.Node, entry.Node.FloatingBounds, false, 0, false, false, windows, decorations);
        return new(windows, decorations);
    }

    public LayoutNode? FindNode(long id)
    {
        foreach (WorkspaceState workspace in Workspaces.Values)
        {
            LayoutNode? found = FindNode(workspace.Root, id) ?? workspace.Floating.Select(root => FindNode(root, id)).FirstOrDefault(node => node is not null);
            if (found is not null) return found;
        }
        return scratchpad.Select(entry => FindNode(entry.Node, id)).FirstOrDefault(node => node is not null);
    }

    private void Render(LayoutNode node, PixelRect rectangle, bool visible, int gap, bool hideBorder, bool fullscreen,
        List<WindowLayout> windows, List<ContainerDecoration> decorations)
    {
        node.Bounds = rectangle;
        if (node is WindowNode window)
        {
            string border = window.IsFloating ? Settings.DefaultFloatingBorder : Settings.DefaultBorder;
            int width = window.IsFloating ? Settings.FloatingBorderWidth : Settings.BorderWidth;
            windows.Add(new(window.WindowId, window.Id, rectangle, visible && rectangle.Width > 0 && rectangle.Height > 0,
                window.IsFloating, fullscreen, hideBorder || fullscreen || border == "none" ? 0 : width));
            return;
        }
        ContainerNode container = (ContainerNode)node;
        int count = container.Children.Count;
        if (count == 0) return;
        if (container.Layout is LayoutKind.Tabbed or LayoutKind.Stacked)
        {
            int header = Math.Min(HeaderHeight, Math.Max(1, rectangle.Height / (container.Layout == LayoutKind.Stacked ? count + 1 : 2)));
            int totalHeader = container.Layout == LayoutKind.Tabbed ? header : header * count;
            PixelRect content = rectangle.Inset(0, totalHeader, 0, 0);
            List<TabLabel> tabs = [];
            for (int i = 0; i < count; i++)
            {
                LayoutNode child = container.Children[i];
                bool active = container.ActiveChild == child;
                PixelRect label = container.Layout == LayoutKind.Tabbed
                    ? new(rectangle.X + rectangle.Width * i / count, rectangle.Y,
                        rectangle.Width * (i + 1) / count - rectangle.Width * i / count, header)
                    : new(rectangle.X, rectangle.Y + header * i, rectangle.Width, header);
                tabs.Add(new(child.Id, TitleOf(child), label, active));
                Render(child, content, visible && active, gap, hideBorder, fullscreen, windows, decorations);
            }
            if (visible) decorations.Add(new(container.Id, container.Layout, tabs));
            return;
        }
        bool horizontal = container.Layout == LayoutKind.SplitHorizontal;
        int dimension = horizontal ? rectangle.Width : rectangle.Height;
        int actualGap = count > 1 ? Math.Min(gap, Math.Max(0, (dimension - count) / (count - 1))) : 0;
        int usable = Math.Max(0, dimension - actualGap * (count - 1));
        double sum = container.Children.Sum(child => Math.Max(.0001, child.Weight));
        double weight = 0;
        int previous = 0;
        for (int i = 0; i < count; i++)
        {
            LayoutNode child = container.Children[i];
            weight += Math.Max(.0001, child.Weight);
            int boundary = i == count - 1 ? usable : (int)Math.Round(usable * weight / sum);
            int size = Math.Max(0, boundary - previous);
            PixelRect bounds = horizontal
                ? new(rectangle.X + previous + actualGap * i, rectangle.Y, size, rectangle.Height)
                : new(rectangle.X, rectangle.Y + previous + actualGap * i, rectangle.Width, size);
            Render(child, bounds, visible, gap, hideBorder, fullscreen, windows, decorations);
            previous = boundary;
        }
    }

    private void FocusNode(LayoutNode node)
    {
        WindowNode? window = ActiveLeaf(node);
        WorkspaceState? workspace = WorkspaceOf(node);
        if (workspace is null) return;
        selected = node;
        FocusedWindow = window;
        if (window is null) return;
        window.Urgent = false;
        workspace.LastFocusedWindowId = window.WindowId;
        if (window.IsFloating) workspace.LastFloatingWindowId = window.WindowId;
        else workspace.LastTilingWindowId = window.WindowId;
        for (LayoutNode branch = window; branch.Parent is { } parent; branch = parent) parent.ActiveChildId = branch.Id;
    }

    private void SelectWorkspaceFocus(WorkspaceState workspace)
    {
        WindowNode? window = workspace.AllWindows().FirstOrDefault(node => node.WindowId == workspace.LastFocusedWindowId)
            ?? ActiveLeaf(workspace.FullscreenNode ?? workspace.Root) ?? workspace.Floating.Select(ActiveLeaf).LastOrDefault(node => node is not null);
        selected = window;
        FocusedWindow = window;
        if (window is not null) FocusNode(window);
    }

    private ContainerNode InsertionParent(WorkspaceState workspace)
    {
        if (workspace == CurrentWorkspace && selected is not null && !selected.IsFloating && WorkspaceOf(selected) == workspace)
            return selected as ContainerNode ?? selected.Parent ?? workspace.Root;
        WindowNode? last = workspace.Root.DescendantWindows().FirstOrDefault(window => window.WindowId == workspace.LastTilingWindowId);
        return last?.Parent ?? workspace.Root;
    }

    private LayoutNode? MovableSelection() => selected == CurrentWorkspace.Root ? CurrentWorkspace.Root.ActiveChild : selected;

    private void Detach(LayoutNode node, WorkspaceState workspace)
    {
        if (workspace.FullscreenNode is not null && ContainsNode(node, workspace.FullscreenNode)) workspace.FullscreenNode = null;
        if (node.Parent is { } parent)
        {
            parent.Children.Remove(node);
            node.Parent = null;
            Normalize(parent, workspace);
        }
        else workspace.Floating.Remove(node);
    }

    private void Normalize(ContainerNode container, WorkspaceState? workspace)
    {
        if (container.Children.All(child => child.Id != container.ActiveChildId)) container.ActiveChildId = container.Children.LastOrDefault()?.Id;
        ContainerNode? parent = container.Parent;
        if (parent is null) return;
        int index = parent.Children.IndexOf(container);
        if (index < 0) return;
        if (container.Children.Count == 0)
        {
            parent.Children.RemoveAt(index);
            container.Parent = null;
            Normalize(parent, workspace);
        }
        else if (container.Children.Count == 1)
        {
            LayoutNode only = container.Children[0];
            only.Weight = container.Weight;
            only.Parent = parent;
            parent.Children[index] = only;
            if (parent.ActiveChildId == container.Id) parent.ActiveChildId = only.Id;
            if (selected == container) selected = only;
            if (workspace?.FullscreenNode == container) workspace.FullscreenNode = only;
            container.Children.Clear();
            container.Parent = null;
        }
    }

    private OutputState OutputFor(WorkspaceState workspace) => Outputs.FirstOrDefault(output => output.Name == workspace.OutputName) ?? Outputs[0];

    private OutputState? NearestOutput(Direction direction)
    {
        PixelRect origin = ActiveOutput.Bounds;
        return Outputs.Where(output => output != ActiveOutput && InDirection(output.Bounds, origin, direction))
            .OrderBy(output => DirectionDistance(output.Bounds, origin, direction)).FirstOrDefault();
    }

    private static WindowNode? NearestWindow(IEnumerable<WindowNode> candidates, LayoutNode origin, Direction direction) => candidates
        .Where(window => !ContainsNode(origin, window) && InDirection(window.Bounds, origin.Bounds, direction))
        .OrderBy(window => DirectionDistance(window.Bounds, origin.Bounds, direction)).FirstOrDefault();

    private static WindowNode? EdgeWindow(IEnumerable<WindowNode> candidates, Direction direction, PixelRect origin) => candidates
        .OrderBy(window => direction switch
        {
            Direction.Right => window.Bounds.X,
            Direction.Left => -window.Bounds.Right,
            Direction.Down => window.Bounds.Y,
            _ => -window.Bounds.Bottom
        }).ThenBy(window => IsHorizontal(direction) ? Math.Abs(window.Bounds.CenterY - origin.CenterY) : Math.Abs(window.Bounds.CenterX - origin.CenterX))
        .FirstOrDefault();

    private static LayoutNode Descend(LayoutNode node, Direction direction, PixelRect origin)
    {
        if (node is not ContainerNode container || container.Children.Count == 0) return node;
        if (container.Layout is LayoutKind.Tabbed or LayoutKind.Stacked || !NavigatesSplit(container.Layout, direction))
            return ActiveLeaf(container) ?? node;
        WindowNode? edge = EdgeWindow(container.DescendantWindows(), direction is Direction.Left ? Direction.Right :
            direction is Direction.Right ? Direction.Left : direction is Direction.Up ? Direction.Down : Direction.Up, origin);
        return edge ?? node;
    }

    private static bool InDirection(PixelRect candidate, PixelRect origin, Direction direction) => direction switch
    {
        Direction.Left => candidate.CenterX < origin.CenterX,
        Direction.Right => candidate.CenterX > origin.CenterX,
        Direction.Up => candidate.CenterY < origin.CenterY,
        _ => candidate.CenterY > origin.CenterY
    };

    private static double DirectionDistance(PixelRect candidate, PixelRect origin, Direction direction)
    {
        int primary = IsHorizontal(direction) ? Math.Abs(candidate.CenterX - origin.CenterX) : Math.Abs(candidate.CenterY - origin.CenterY);
        int secondary = IsHorizontal(direction) ? Math.Abs(candidate.CenterY - origin.CenterY) : Math.Abs(candidate.CenterX - origin.CenterX);
        bool overlaps = IsHorizontal(direction) ? candidate.Y < origin.Bottom && candidate.Bottom > origin.Y : candidate.X < origin.Right && candidate.Right > origin.X;
        return primary + secondary * 2 + (overlaps ? 0 : 100000);
    }

    private static bool IsHorizontal(Direction direction) => direction is Direction.Left or Direction.Right;
    private static int DirectionStep(Direction direction) => direction is Direction.Left or Direction.Up ? -1 : 1;
    private static bool NavigatesSplit(LayoutKind layout, Direction direction) => layout == (IsHorizontal(direction) ? LayoutKind.SplitHorizontal : LayoutKind.SplitVertical);
    private static bool Navigates(LayoutKind layout, Direction direction) => NavigatesSplit(layout, direction)
        || (layout == LayoutKind.Tabbed && IsHorizontal(direction)) || (layout == LayoutKind.Stacked && !IsHorizontal(direction));
    private static int WorkspaceNumber(string name) => int.TryParse(name.Split(':')[0], out int number) ? number : int.MaxValue;

    private static void ChangeLayout(ContainerNode node, LayoutKind layout)
    {
        node.Layout = layout;
        if (layout is LayoutKind.SplitHorizontal or LayoutKind.SplitVertical) node.LastSplitLayout = layout;
    }

    private static LayoutNode FloatingRoot(LayoutNode node)
    {
        while (node.Parent is { IsFloating: true } parent) node = parent;
        return node;
    }

    private static WindowNode? ActiveLeaf(LayoutNode node) => node is WindowNode window ? window :
        node is ContainerNode { ActiveChild: { } child } ? ActiveLeaf(child) : null;

    private static string TitleOf(LayoutNode node) => node is WindowNode window ? window.Title :
        $"[{string.Join(" | ", node.DescendantWindows().Select(leaf => leaf.Title))}]";

    private static bool ContainsNode(LayoutNode root, LayoutNode node) => root == node || root is ContainerNode container && container.Children.Any(child => ContainsNode(child, node));

    private static LayoutNode? FindNode(LayoutNode root, long id) => root.Id == id ? root : root is ContainerNode container
        ? container.Children.Select(child => FindNode(child, id)).FirstOrDefault(child => child is not null) : null;

    private static int VisibleLeafCount(LayoutNode node) => node is WindowNode ? 1 : node is ContainerNode container
        ? container.Layout is LayoutKind.Tabbed or LayoutKind.Stacked
            ? container.ActiveChild is { } active ? VisibleLeafCount(active) : 0
            : container.Children.Sum(VisibleLeafCount) : 0;

    private static void MarkFloating(LayoutNode node, bool value)
    {
        node.IsFloating = value;
        if (node is ContainerNode container) foreach (LayoutNode child in container.Children) MarkFloating(child, value);
    }

    private static void MarkScratchpad(LayoutNode node, bool value)
    {
        node.IsScratchpad = value;
        if (node is ContainerNode container) foreach (LayoutNode child in container.Children) MarkScratchpad(child, value);
    }
}

using System.Diagnostics;
using SwayWin.Core.Configuration;

namespace SwayWin.Tests;

public static class ConfigTests
{
    public static IEnumerable<(string Name, Action Run)> Cases()
    {
        yield return ("Config model defaults and ordinal variables", ModelDefaults);
        yield return ("Sway quotes, escapes, and Windows paths", QuotesAndPaths);
        yield return ("Sway command separators, colors, and comments", CommandsAndComments);
        yield return ("Includes preserve order, Unix paths, and sorted globs", IncludeOrderAndPaths);
        yield return ("Missing optional includes and commented autostart", MissingAndCommentedIncludes);
        yield return ("Include cycles and depth are bounded", CyclesAndDepth);
        yield return ("Continuations preserve commands and source line numbers", ContinuationsAndLines);
        yield return ("Final Windows overrides expand original keys and commands", FinalVariableOverrides);
        yield return ("Inline modes and includes preserve block context", ModesAndContext);
        yield return ("Window and bar settings import", SettingsAndBar);
        yield return ("Window criteria match native identities", CriteriaMatching);
        yield return ("Invalid criteria and regex timeout fail safely", CriteriaFailures);
        yield return ("Rules, assignments, no_focus, and output mappings", RulesAndMappings);
        yield return ("Linux directives diagnose without execution", LinuxDiagnosticsAndStartup);
        yield return ("Binding flags and Linux XKB keycode mapping", BindingFlagsAndCodes);
        yield return ("Malformed and unknown config produces source diagnostics", MalformedConfiguration);
    }

    private static void ModelDefaults()
    {
        var config = new SwayConfig();
        Equal(8, config.Settings.GapInner);
        Equal(8, config.Settings.GapOuter);
        Equal<int?>(null, config.Settings.GapTop);
        Equal("off", config.Settings.SmartGaps);
        Equal("none", config.Settings.HideEdgeBorders);
        Equal("pixel", config.Settings.DefaultBorder);
        Equal(2, config.Settings.BorderWidth);
        Equal("normal", config.Settings.DefaultFloatingBorder);
        Equal(2, config.Settings.FloatingBorderWidth);
        True(config.Settings.FocusFollowsMouse && config.Settings.MouseWarping, "Mouse defaults.");
        Equal("Mod4", config.Settings.FloatingModifier);
        Equal("splith", config.Settings.WorkspaceLayout);
        True(!config.Settings.WorkspaceAutoBackAndForth, "Default back-and-forth.");
        Equal(75, config.Settings.MinimumFloatingWidth);
        Equal(50, config.Settings.MinimumFloatingHeight);
        Equal(0, config.Settings.MaximumFloatingWidth);
        Equal(0, config.Settings.MaximumFloatingHeight);
        Equal(25, config.Settings.RepeatRate);
        Equal(600, config.Settings.RepeatDelay);
        True(config.Bar.Enabled, "Bar enabled by default.");
        Equal("bottom", config.Bar.Position);
        Equal(28, config.Bar.Height);
        Equal("dock", config.Bar.Mode);
        Equal("", config.Bar.StatusCommand);
        config.Variables["$Term"] = "one";
        config.Variables["$term"] = "two";
        config.Variables["$term10"] = "$term --new";
        Equal("one two two --new $terminal $(date)", config.Expand("$Term $term $term10 $terminal $(date)"));
        Equal("two", config.Expand("${term}"));
    }

    private static void QuotesAndPaths()
    {
        var tokens = SwaySyntax.Tokenize("""exec "C:\Program Files\Tools\tool.exe" 'sh -c "echo a;b,c"' plain\ value \\server\share """ + "''");
        Sequence(["exec", @"C:\Program Files\Tools\tool.exe", "sh -c \"echo a;b,c\"", "plain value", @"\\server\share", ""], tokens);
        Equal(@"C:\Tools\app.exe", SwaySyntax.Unquote("\"C:\\Tools\\app.exe\""));
        Equal("echo \"hello\"", SwaySyntax.Unquote("\"echo \\\"hello\\\"\""));
        Equal(@"\d+\.exe", SwaySyntax.Tokenize("'\\d+\\.exe'").Single());
        Equal("one two", SwaySyntax.Tokenize("one\\ two").Single());
        Equal("\"one\" \"two\"", SwaySyntax.Unquote("\"one\" \"two\""));
    }

    private static void CommandsAndComments()
    {
        var commands = SwaySyntax.SplitCommands("""exec "echo a;b,c", [title="a,b;[c]"] focus; exec 'sh -c "echo x;y,#abc"'""");
        Sequence(["exec \"echo a;b,c\"", "[title=\"a,b;[c]\"] focus", "exec 'sh -c \"echo x;y,#abc\"'"], commands);
        Sequence([@"exec echo a\;b", "focus left"], SwaySyntax.SplitCommands(@"exec echo a\;b, focus left;"));
        Equal("client.focused #000000 #0066ff #ffffff #0044ff00", SwaySyntax.StripComment("client.focused #000000 #0066ff #ffffff #0044ff00 # actual comment"));
        Equal("set $bg #000000", SwaySyntax.StripComment("set $bg #000000 # actual comment"));
        Equal("exec \"echo #not-a-comment\"", SwaySyntax.StripComment("exec \"echo #not-a-comment\" # actual comment"));
        Equal("", SwaySyntax.StripComment("# include config.d/30-autostart.conf"));
        var extracted = SwaySyntax.ExtractCriteria("""[title="a,b;[c]" app_id="^editor$"] focus left, border pixel 2""");
        True(extracted.Criteria is { IsValid: true }, "Criteria with quoted brackets should be valid.");
        Equal("a,b;[c]", extracted.Criteria!.Fields["title"]);
        Equal("focus left, border pixel 2", extracted.Command);
        Equal<WindowCriteria?>(null, SwaySyntax.ExtractCriteria("focus left").Criteria);
    }

    private static void IncludeOrderAndPaths() => WithTemp(files =>
    {
        const string home = "home/ex4zy/.config/sway/";
        var root = files.Write(home + "config", """
            set $order root
            include ~/.config/sway/config.d/[0-2][0-9]-*.conf
            include config.d/40-bindings.conf
            include /etc/sway/config.d/*.conf
            include "extras with spaces/*"
            """);
        var twenty = files.Write(home + "config.d/20-input.conf", "set $order twenty\ninput * repeat_delay 410\n");
        var zero = files.Write(home + "config.d/00-variables.conf", "set $mod Mod4\nset $order zero\n");
        var ten = files.Write(home + "config.d/10-appearance.conf", "set $order ten\ngaps inner 6\n");
        var forty = files.Write(home + "config.d/40-bindings.conf", "bindsym $mod+Return exec $term\n");
        var system = files.Write("etc/sway/config.d/90-system.conf", "set $order system\n");
        var extra = files.Write(home + "extras with spaces/live", "set $order last\n");
        files.Write(home + "extras with spaces/live.bak", "exec forbidden-backup\n");
        files.Write(home + "extras with spaces/live~", "exec forbidden-backup\n");
        files.Write(home + "config.d/99-unreferenced.conf", "exec forbidden-unreferenced\n");
        var config = new SwayConfigLoader().Load(Path.GetDirectoryName(root)!);
        NoErrors(config);
        Equal(root, config.SourcePath);
        Sequence([root, zero, ten, twenty, forty, system, extra], config.SourceFiles);
        Equal("last", config.Expand("$order"));
        Equal(6, config.Settings.GapInner);
        Equal(410, config.Settings.RepeatDelay);
        Equal("Mod4+Return", config.Bindings.Single().Keys);
        Equal(0, config.StartupCommands.Count);
    });

    private static void MissingAndCommentedIncludes() => WithTemp(files =>
    {
        var root = files.Write("home/ex4zy/.config/sway/config", """
            # include config.d/30-autostart.conf
            include /etc/sway/config.d/*
            bindsym Mod4+1 workspace number 1
            """);
        files.Write("home/ex4zy/.config/sway/config.d/30-autostart.conf", "exec forbidden-autostart\n");
        files.Write("etc/sway/config", "exec forbidden-substitute\n");
        var config = new SwayConfigLoader().Load(root);
        NoErrors(config);
        Equal(1, config.SourceFiles.Count);
        Equal(0, config.StartupCommands.Count);
        Equal(0, config.ReloadCommands.Count);
        var diagnostic = config.Diagnostics.Single(item => item.Message.Contains("wildcard", StringComparison.OrdinalIgnoreCase));
        Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Equal(root, diagnostic.File);
        Equal(2, diagnostic.Line);

        var missing = new SwayConfigLoader().Load(Path.Combine(files.Root, "missing"));
        HasDiagnostic(missing, DiagnosticSeverity.Error, "not found");
        var mandatory = files.Write("mandatory", "include absent.conf\ngaps inner 12\n");
        var nonWildcard = new SwayConfigLoader().Load(mandatory);
        HasDiagnostic(nonWildcard, DiagnosticSeverity.Error, "not found");
        Equal(12, nonWildcard.Settings.GapInner);
    });

    private static void CyclesAndDepth() => WithTemp(files =>
    {
        var root = files.Write("config", "include child.conf\ngaps inner 11\n");
        var child = files.Write("child.conf", "include ./config\nbindsym Mod4+x focus left\n");
        var config = new SwayConfigLoader().Load(root);
        HasDiagnostic(config, DiagnosticSeverity.Error, "cycle");
        Sequence([root, child], config.SourceFiles);
        Equal(11, config.Settings.GapInner);
        Equal(1, config.Bindings.Count);
        for (var i = 0; i < 70; i++)
            files.Write($"depth/{i}.conf", i == 69 ? "gaps inner 99" : $"include {i + 1}.conf\n");
        var deep = new SwayConfigLoader().Load(Path.Combine(files.Root, "depth", "0.conf"));
        HasDiagnostic(deep, DiagnosticSeverity.Error, "depth limit");
        True(deep.SourceFiles.Count <= 64, "Include depth must remain bounded.");
    });

    private static void ContinuationsAndLines() => WithTemp(files =>
    {
        var root = files.Write("config", """
            set $mod Mod4
            bindsym $mod+Return exec \
                "C:\Program Files\Tools\tool.exe" \
                --title "a;b,# kept" # stripped comment
            mode "resize" { \
                bindsym Return mode "default" \
            }
            bindsym $mod+x exec "first \
                second # inside" # comment after closing quote
            """);
        var config = new SwayConfigLoader().Load(root);
        NoErrors(config);
        var launch = config.Bindings.Single(binding => binding.Keys == "Mod4+Return");
        Equal(root, launch.SourcePath);
        Equal(2, launch.LineNumber);
        True(launch.Command.Contains(@"C:\Program Files\Tools\tool.exe", StringComparison.Ordinal), "Windows path must be unchanged.");
        True(launch.Command.Contains("a;b,# kept", StringComparison.Ordinal), "Quoted delimiters must be unchanged.");
        True(!launch.Command.Contains("stripped comment", StringComparison.Ordinal), "Actual comment must be removed.");
        var exit = config.Bindings.Single(binding => binding.Mode == "resize");
        Equal(5, exit.LineNumber);
        Equal("mode \"default\"", exit.Command);
        var quoted = config.Bindings.Single(binding => binding.Keys == "Mod4+x");
        Equal(8, quoted.LineNumber);
        True(quoted.Command.EndsWith("second # inside\"", StringComparison.Ordinal), "Comment detection must retain quote state across continuations.");
    });

    private static void FinalVariableOverrides() => WithTemp(files =>
    {
        var root = files.Write("original/config", """
            set $mod Mod4
            set $term ghostty
            set $menu wofi --show run --allow-images
            set $launcher $menu
            set $ws 10
            bindsym --to-code $mod+Return exec $term --title "a;b"
            bindsym $mod+a exec $launcher
            bindsym $mod+0 workspace number $ws
            exec $term
            exec_always $menu
            """);
        var overrides = files.Write("windows/config", """
            set $mod Mod1
            set $term wt.exe -w new
            include launcher.conf
            set $ws 9
            """);
        var launcher = files.Write("windows/launcher.conf", "set $menu swaywin-launcher\n");
        var config = new SwayConfigLoader().Load(root, Path.GetDirectoryName(overrides));
        NoErrors(config);
        Sequence([root, overrides, launcher], config.SourceFiles);
        var terminal = config.Bindings.Single(binding => binding.Keys == "Mod1+Return");
        True(terminal.ToCode, "--to-code retained.");
        Equal("exec $term --title \"a;b\"", terminal.Command);
        Equal("exec wt.exe -w new --title \"a;b\"", config.Expand(terminal.Command));
        Equal("exec swaywin-launcher", config.Expand(config.Bindings.Single(binding => binding.Keys == "Mod1+a").Command));
        Equal("workspace number 9", config.Expand(config.Bindings.Single(binding => binding.Keys == "Mod1+0").Command));
        Sequence(["$term", "$menu"], config.StartupCommands);
        Sequence(["$menu"], config.ReloadCommands);
        Equal("wt.exe -w new", config.Expand(config.StartupCommands[0]));
        Equal("swaywin-launcher", config.Expand(config.ReloadCommands.Single()));
        config.Variables["$term"] = "notepad.exe";
        Equal("exec notepad.exe --title \"a;b\"", config.Expand(terminal.Command));
    });

    private static void ModesAndContext() => WithTemp(files =>
    {
        var root = files.Write("config", """
            mode "resize" {
                include nested/resize.conf
            }
            mode "one" { bindsym F1 focus left } mode "two" { bindsym F2 focus right }
            bar { colors { background #000000 } position top; height 24 }
            bindsym Mod4+x focus left
            bindsym Mod4+x focus right
            """);
        var resize = files.Write("nested/resize.conf", """
            bindsym h resize shrink width 20 px; bindsym Return mode "default"
            bindsym Escape mode "default"
            """);
        var config = new SwayConfigLoader().Load(root);
        NoErrors(config);
        Equal(3, config.Bindings.Count(binding => binding.Mode == "resize"));
        True(config.Bindings.Where(binding => binding.Mode == "resize").All(binding => binding.SourcePath == resize), "Included bindings should retain their own source.");
        Equal("focus left", config.Bindings.Single(binding => binding.Mode == "one").Command);
        Equal("focus right", config.Bindings.Single(binding => binding.Mode == "two").Command);
        Equal("focus right", config.Bindings.Single(binding => binding.Keys == "Mod4+x").Command);
        Equal("top", config.Bar.Position);
        Equal(24, config.Bar.Height);
        Equal(1, config.Diagnostics.Count(item => item.Message.Contains("colors/client", StringComparison.Ordinal)));
    });

    private static void SettingsAndBar() => WithTemp(files =>
    {
        var root = files.Write("config", """
            set $mod Mod4
            set $gap 6
            set $font monospace 10
            gaps inner $gap
            gaps outer 0
            gaps horizontal 3
            gaps vertical 4
            gaps top 1
            gaps right 2
            smart_gaps inverse
            smart_borders on
            hide_edge_borders smart
            default_border pixel 3
            default_floating_border normal 4
            focus_follows_mouse no
            mouse_warping none
            floating_modifier $mod normal
            floating_minimum_size 80 x 60
            floating_maximum_size 1200 x 900
            workspace_layout stacked
            workspace_auto_back_and_forth yes
            input type:keyboard { repeat_rate 30; repeat_delay 350 }
            bar {
                position top
                height 24
                mode hide
                font pango:$font
                separator_symbol " | "
                workspace_min_width 24
                strip_workspace_numbers yes
                status_command while date; do sleep 1; done
            }
            """);
        var config = new SwayConfigLoader().Load(root);
        NoErrors(config);
        var s = config.Settings;
        Equal(6, s.GapInner); Equal(0, s.GapOuter);
        Equal<int?>(1, s.GapTop); Equal<int?>(4, s.GapBottom);
        Equal<int?>(3, s.GapLeft); Equal<int?>(2, s.GapRight);
        Equal("inverse", s.SmartGaps); True(s.SmartBorders, "Smart borders imported.");
        Equal("smart", s.HideEdgeBorders);
        Equal("pixel", s.DefaultBorder); Equal(3, s.BorderWidth);
        Equal("normal", s.DefaultFloatingBorder); Equal(4, s.FloatingBorderWidth);
        True(!s.FocusFollowsMouse && !s.MouseWarping, "Mouse options imported.");
        Equal("Mod4", s.FloatingModifier);
        Equal(80, s.MinimumFloatingWidth); Equal(60, s.MinimumFloatingHeight);
        Equal(1200, s.MaximumFloatingWidth); Equal(900, s.MaximumFloatingHeight);
        Equal("stacking", s.WorkspaceLayout); True(s.WorkspaceAutoBackAndForth, "Back-and-forth imported.");
        Equal(30, s.RepeatRate); Equal(350, s.RepeatDelay);
        Equal("top", config.Bar.Position); Equal(24, config.Bar.Height);
        Equal("hide", config.Bar.Mode); True(config.Bar.Enabled, "Hide mode still has a bar.");
        Equal("pango:monospace 10", config.Bar.Font); Equal(" | ", config.Bar.SeparatorSymbol);
        Equal(24, config.Bar.WorkspaceMinWidth); True(config.Bar.StripWorkspaceNumbers, "Bar strip numbers.");
        Equal("while date; do sleep 1; done", config.Bar.StatusCommand);
        True(!config.Diagnostics.Any(item => item.Message.Contains("Unknown", StringComparison.Ordinal)), "Known settings must not be diagnosed as unknown.");

        var reset = files.Write("reset", "gaps top 10\ngaps outer 7\nfloating_maximum_size -1 x -1\nbar { mode invisible }\n");
        var resetConfig = new SwayConfigLoader().Load(reset);
        NoErrors(resetConfig);
        Equal<int?>(null, resetConfig.Settings.GapTop);
        Equal(7, resetConfig.Settings.GapOuter);
        Equal(0, resetConfig.Settings.MaximumFloatingWidth);
        True(!resetConfig.Bar.Enabled, "Invisible bar should be disabled.");
    });

    private static void CriteriaMatching()
    {
        var identity = new WindowIdentity(42, "Editor - draft 123", "EditorWindow", "Editor.exe", true, "2: code", true);
        var criteria = WindowCriteria.Parse("""[app_id="^editor$" instance="^editor\.exe$" class="^EditorWindow$" title="draft \d+" con_id=42 floating workspace="^2:" urgent=yes]""");
        True(criteria.IsValid, string.Join("; ", criteria.ValidationErrors));
        True(criteria.Matches(identity), "Native fields should all match.");
        True(!criteria.Matches(identity with { Floating = false }), "Floating constraint should reject tiled window.");
        True(!criteria.Matches(identity with { Urgent = false }), "Urgent constraint should reject nonurgent window.");
        True(!criteria.Matches(identity with { Id = 43 }), "ID constraint should reject other window.");
        True(WindowCriteria.Parse("[tiling urgent=no id=7]").Matches(new WindowIdentity(7, "", "", "")), "Bare tiling and nonurgent match.");
        True(WindowCriteria.Parse("[app_id=\"^editor\\.exe$\"]").Matches(identity with { ProcessName = "editor" }), "Process criteria should accept .exe suffix with extensionless native names.");
        var focused = WindowCriteria.Parse("[con_id=__focused__ title=__focused__ workspace=__focused__]");
        True(!focused.Matches(identity), "Focused sentinel needs a focused identity.");
        True(focused.Matches(identity, identity), "Focused sentinel should match the focused window.");
        True(!focused.Matches(identity, identity with { Id = 99 }), "Focused sentinel should reject another ID.");
        True(WindowCriteria.Parse("[]").Matches(identity), "Empty criteria mean any window.");
    }

    private static void CriteriaFailures()
    {
        var identity = new WindowIdentity(1, "sample", "Window", "editor");
        foreach (var raw in new[] { "[title=\"[\"]", "[unknown=foo]", "[id=abc]", "[urgent=latest]", "[floating=maybe]", "[title=foo title=bar]", "[title]", "[title=\"unfinished]" })
        {
            var invalid = WindowCriteria.Parse(raw);
            True(!invalid.IsValid && invalid.ValidationErrors.Count > 0, $"Invalid criteria should report '{raw}'.");
            True(!invalid.Matches(identity), "Invalid criteria must never become broad matching rules.");
        }
        True(!SwaySyntax.ExtractCriteria("[title=\"x\" focus left").Criteria!.IsValid, "Unclosed criteria should report safely.");
        var pathological = WindowCriteria.Parse("[title=\"^(a+)+$\"]");
        True(pathological.IsValid, "Pathological pattern is syntactically valid.");
        var watch = Stopwatch.StartNew();
        True(!pathological.Matches(identity with { Title = new string('a', 20_000) + "!" }), "Regex timeout must fail closed.");
        watch.Stop();
        True(watch.Elapsed < TimeSpan.FromSeconds(2), "Regex matching must be bounded by its timeout.");
        True(WindowCriteria.RegexTimeout > TimeSpan.Zero && WindowCriteria.RegexTimeout < TimeSpan.FromSeconds(1), "Regex must have a short finite timeout.");
    }

    private static void RulesAndMappings() => WithTemp(files =>
    {
        var root = files.Write("config", """
            set $app editor
            set $ws "2: code"
            set $term ghostty
            for_window [app_id="^$app$" title="draft"] floating enable, exec $term
            assign [class="^EditorWindow$"] -> workspace $ws
            no_focus [app_id="^updater$"]
            workspace $ws output DP-1
            windows_output DP-1 "\\.\DISPLAY2"
            for_window [title="["] kill
            for_window [unsupported=foo] kill
            """);
        var overrides = files.Write("overrides", "set $app notepad\nset $term wt.exe\n");
        var config = new SwayConfigLoader().Load(root, overrides);
        NoErrors(config);
        Equal(3, config.WindowRules.Count);
        var rule = config.WindowRules[0];
        True(rule.Criteria.Matches(new WindowIdentity(1, "draft", "EditorWindow", "notepad.exe")), "Criteria should expand final variables.");
        Equal("floating enable, exec $term", rule.Command);
        Equal("floating enable, exec wt.exe", config.Expand(rule.Command));
        Equal(root, rule.SourcePath); Equal(4, rule.LineNumber);
        Equal("2: code", config.Assignments.Single().Workspace);
        Equal(1, config.NoFocusRules.Count);
        Equal("DP-1", config.WorkspaceOutputs["2: code"]);
        Equal(@"\\.\DISPLAY2", config.OutputMappings["DP-1"]);
        HasDiagnostic(config, DiagnosticSeverity.Warning, "Invalid regex");
        HasDiagnostic(config, DiagnosticSeverity.Warning, "Unsupported window criterion");
        True(!config.WindowRules[1].Criteria.Matches(new WindowIdentity(1, "anything", "", "")), "Invalid rule should fail closed.");
    });

    private static void LinuxDiagnosticsAndStartup() => WithTemp(files =>
    {
        var marker = Path.Combine(files.Root, "must-not-exist");
        var root = files.Write("config", """
            input type:keyboard {
                xkb_layout us,ru
                xkb_options grp:win_space_toggle
                repeat_rate 25
                repeat_delay 600
            }
            seat * { hide_cursor 5000 }
            output DVI-D-1 disable
            output DP-1 { mode 1920x1080@143Hz }
            output * bg #000000 solid_color
            client.focused #000000 #0066ff #ffffff
            client.unfocused #000000 #000000 #777777
            bar { colors { background #000000 focused_workspace #000000 #0066ff #ffffff } status_command while date; do sleep 1; done }
            exec swaybg -c #000000
            exec_always --no-startup-id $term
            """ + $"\nexec cmd.exe /c echo forbidden > \"{marker}\"\n");
        var config = new SwayConfigLoader().Load(root);
        NoErrors(config);
        Equal(3, config.StartupCommands.Count);
        Sequence(["$term"], config.ReloadCommands);
        Equal(3, config.StartupEntries.Count);
        True(!config.StartupEntries[0].Always && config.StartupEntries[1].Always && !config.StartupEntries[2].Always, "exec_always metadata must distinguish reload commands.");
        True(!File.Exists(marker), "The loader must never run commands.");
        HasDiagnostic(config, DiagnosticSeverity.Warning, "Windows-native keyboard");
        HasDiagnostic(config, DiagnosticSeverity.Warning, "Linux seat");
        HasDiagnostic(config, DiagnosticSeverity.Warning, "Linux output");
        HasDiagnostic(config, DiagnosticSeverity.Warning, "wallpaper");
        HasDiagnostic(config, DiagnosticSeverity.Warning, "status_command");
        Equal(1, config.Diagnostics.Count(item => item.Message.Contains("colors/client", StringComparison.Ordinal)));
        True(!config.Diagnostics.Any(item => item.Message.Contains("Unknown", StringComparison.Ordinal)), "Recognized Linux directives should explain compatibility, not disappear as unknown.");
    });

    private static void BindingFlagsAndCodes() => WithTemp(files =>
    {
        var root = files.Write("config", """
            set $mod Mod4
            bindsym --to-code --release --no-repeat --locked $mod+Return exec $term; exec $menu
            bindcode --locked $mod+38 focus left
            bindcode $mod+111 focus up
            bindcode $mod+9999 focus right
            bindsym $mod+Right focus right
            """);
        var config = new SwayConfigLoader().Load(root);
        NoErrors(config);
        Equal(4, config.Bindings.Count);
        var flags = config.Bindings.Single(binding => binding.Keys == "Mod4+Return");
        True(flags.ToCode && flags.Release && flags.NoRepeat && flags.Locked, "All binding flags must be retained.");
        Equal("exec $term; exec $menu", flags.Command);
        var code = config.Bindings.Single(binding => binding.Keys == "Mod4+a");
        True(code.ToCode && code.Locked, "bindcode must use physical-key matching.");
        True(config.Bindings.Any(binding => binding.Keys == "Mod4+Up" && binding.ToCode), "Linux arrow XKB code mapping.");
        HasDiagnostic(config, DiagnosticSeverity.Warning, "cannot be mapped");
        HasDiagnostic(config, DiagnosticSeverity.Info, "XKB keycode 38");
    });

    private static void MalformedConfiguration() => WithTemp(files =>
    {
        var root = files.Write("config", """
            set $a $b
            set $b $a
            unknown_directive value
            gaps inner nope
            bindsym Mod4+x
            for_window missing-criteria kill
            mode resize {
                bindsym Return mode "default"
            """);
        var config = new SwayConfigLoader().Load(root);
        HasDiagnostic(config, DiagnosticSeverity.Warning, "Cyclic");
        HasDiagnostic(config, DiagnosticSeverity.Warning, "Unknown configuration directive");
        HasDiagnostic(config, DiagnosticSeverity.Error, "Expected gaps");
        HasDiagnostic(config, DiagnosticSeverity.Error, "Binding requires");
        HasDiagnostic(config, DiagnosticSeverity.Error, "requires bracketed");
        HasDiagnostic(config, DiagnosticSeverity.Error, "Unclosed");
        Equal(8, config.Settings.GapInner);
        True(config.Expand("$a").Length < 32, "Cyclic variables should remain bounded.");
        True(config.Diagnostics.All(item => item.File == root && item.Line > 0), "Parser diagnostics must retain source locations.");
    });

    private static void WithTemp(Action<TempFiles> run)
    {
        var root = Path.Combine(Path.GetTempPath(), "SwayWin.ConfigTests." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            run(new TempFiles(root));
        }
        finally
        {
            Directory.Delete(root, true);
        }
    }

    private sealed class TempFiles(string root)
    {
        public string Root { get; } = root;

        public string Write(string relativePath, string content)
        {
            var path = Path.Combine(Root, relativePath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content);
            return path;
        }
    }

    private static void NoErrors(SwayConfig config) => True(!config.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error),
        string.Join(Environment.NewLine, config.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error).Select(item => $"{item.File}:{item.Line}: {item.Message}")));

    private static void HasDiagnostic(SwayConfig config, DiagnosticSeverity severity, string text) => True(
        config.Diagnostics.Any(item => item.Severity == severity && item.Message.Contains(text, StringComparison.OrdinalIgnoreCase)),
        $"Missing {severity} diagnostic containing '{text}'.");

    private static void Equal<T>(T expected, T actual)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
            throw new InvalidOperationException($"Expected '{expected}', got '{actual}'.");
    }

    private static void Sequence(IEnumerable<string> expected, IEnumerable<string> actual)
    {
        if (!expected.SequenceEqual(actual, StringComparer.Ordinal))
            throw new InvalidOperationException($"Expected [{string.Join(", ", expected)}], got [{string.Join(", ", actual)}].");
    }

    private static void True(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}

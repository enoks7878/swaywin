using SwayWin.Core.Configuration;
using SwayWin.Tests;

int failed = 0;
int passed = 0;
foreach ((string name, Action run) in ConfigTests.Cases().Concat(LayoutTests.Cases()))
{
    try { run(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception exception) { Console.Error.WriteLine($"FAIL {name}: {exception}"); failed++; }
}
if (args.Length == 2 && args[0] == "--config")
{
    try
    {
        SwayConfig config = new SwayConfigLoader().Load(args[1]);
        LayoutTests.Assert(!config.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error), "Original config has parse errors.");
        LayoutTests.Equal(6, config.Settings.GapInner);
        LayoutTests.Equal(0, config.Settings.GapOuter);
        LayoutTests.Equal("on", config.Settings.SmartGaps);
        LayoutTests.Equal("smart", config.Settings.HideEdgeBorders);
        LayoutTests.Equal(false, config.Settings.MouseWarping);
        LayoutTests.Equal(true, config.Settings.WorkspaceAutoBackAndForth);
        LayoutTests.Equal("top", config.Bar.Position);
        LayoutTests.Equal(24, config.Bar.Height);
        LayoutTests.Equal(10, config.Bindings.Count(binding => binding.Mode == "resize"));
        LayoutTests.Assert(config.Bindings.Any(binding => binding.Keys.Equals("Mod4+Right", StringComparison.OrdinalIgnoreCase)
            && config.Expand(binding.Command) == "focus right"), "Directional binding not imported.");
        LayoutTests.Equal(1, config.StartupCommands.Count);
        LayoutTests.Assert(!config.SourceFiles.Any(file => file.EndsWith("30-autostart.conf", StringComparison.OrdinalIgnoreCase)), "Commented autostart was imported.");
        Console.WriteLine($"PASS real Sway config: {config.SourceFiles.Count} sources, {config.Bindings.Count} bindings");
        passed++;
    }
    catch (Exception exception) { Console.Error.WriteLine($"FAIL real Sway config: {exception}"); failed++; }
}
Console.WriteLine($"{passed} passed, {failed} failed");
return failed == 0 ? 0 : 1;

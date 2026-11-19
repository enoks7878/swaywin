using System.Text;
using System.Text.Json;
using SwayWin.Core.Configuration;

namespace SwayWin;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 3 && args[0] == "--watchdog" && int.TryParse(args[1], out int owner))
            return Recovery.RunWatchdog(owner, args[2]);
        AppOptions options;
        try { options = AppOptions.Parse(args); }
        catch (ArgumentException exception) { AttachOutput(); Console.Error.WriteLine(exception.Message); return 2; }
        if (options.Verb is not null) AttachOutput();
        if (options.Verb == "help") { PrintHelp(); return 0; }
        if (options.Verb is "command" or "get_tree" or "get_workspaces" or "get_outputs" or "reload" or "exit")
        {
            try
            {
                string request = options.Verb == "command" ? options.Command : options.Verb;
                string response = CommandServer.Send(request).GetAwaiter().GetResult();
                Console.WriteLine(response);
                using JsonDocument parsed = JsonDocument.Parse(response);
                return parsed.RootElement.ValueKind == JsonValueKind.Array && parsed.RootElement.EnumerateArray().Any(item =>
                    item.TryGetProperty("success", out JsonElement success) && success.ValueKind == JsonValueKind.False) ? 1 : 0;
            }
            catch (Exception exception) when (exception is IOException or OperationCanceledException or JsonException)
            { Console.Error.WriteLine($"SwayWin is not responding in this Windows session: {exception.Message}"); return 1; }
        }
        if (options.Verb == "recover")
        {
            using Mutex recoveryGate = new(false, CommandServer.MutexName);
            bool locked;
            try { locked = recoveryGate.WaitOne(0); }
            catch (AbandonedMutexException) { locked = true; }
            if (!locked) { Console.Error.WriteLine("Recovery refused: SwayWin is running in this session."); return 1; }
            try
            {
                int count = Recovery.RestoreJournal(Recovery.JournalPath);
                Console.WriteLine(count < 0 ? "Recovery incomplete: owner is alive, journal is invalid, or an application is unresponsive. Remaining records are retained." : $"Restored {count} windows.");
                return count < 0 ? 1 : 0;
            }
            finally { recoveryGate.ReleaseMutex(); }
        }
        SwayConfig config = new SwayConfigLoader().Load(options.ConfigPath, options.OverridesPath);
        if (options.Verb == "check-config") return CheckConfig(config);
        if (config.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error))
        {
            MessageBox.Show(string.Join("\n", config.Diagnostics.Where(item => item.Severity == DiagnosticSeverity.Error)
                .Select(item => $"{item.File}:{item.Line}: {item.Message}")), "SwayWin configuration error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return 2;
        }
        Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        if (options.Verb == "smoke-test") return SmokeTests.Run();
        using Mutex singleInstance = new(false, CommandServer.MutexName);
        bool acquired;
        try { acquired = singleInstance.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            MessageBox.Show("SwayWin is already running in this Windows session. Use its tray menu or the command interface.", "SwayWin", MessageBoxButtons.OK, MessageBoxIcon.Information);
            return 1;
        }
        int recovery = Recovery.RestoreJournal(Recovery.JournalPath);
        if (recovery < 0 || File.Exists(Recovery.JournalPath))
        {
            MessageBox.Show("A previous recovery journal could not be fully restored. It will not be overwritten. Retry --recover after the affected applications respond.",
                "SwayWin recovery pending", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            singleInstance.ReleaseMutex();
            return 2;
        }
        using WindowManagerContext manager = new(config, options);
        Application.ThreadException += (_, error) => { AppLog.Write(error.Exception.ToString()); manager.Stop(); };
        AppDomain.CurrentDomain.UnhandledException += (_, error) => AppLog.Write(error.ExceptionObject.ToString() ?? "Unhandled exception");
        try { Application.Run(manager); }
        finally { manager.Stop(); singleInstance.ReleaseMutex(); }
        return 0;
    }

    private static int CheckConfig(SwayConfig config)
    {
        Console.WriteLine($"Configuration: {config.SourcePath}");
        foreach (string path in config.SourceFiles) Console.WriteLine($"  include: {path}");
        Console.WriteLine($"Bindings: {config.Bindings.Count}; modes: {string.Join(", ", config.Bindings.Select(binding => binding.Mode).Distinct())}");
        Console.WriteLine($"Gaps: inner {config.Settings.GapInner}, outer {config.Settings.GapOuter}, smart {config.Settings.SmartGaps}; borders {config.Settings.DefaultBorder} {config.Settings.BorderWidth}");
        Console.WriteLine($"Focus follows mouse: {config.Settings.FocusFollowsMouse}; mouse warping: {config.Settings.MouseWarping}; workspace back-and-forth: {config.Settings.WorkspaceAutoBackAndForth}");
        Console.WriteLine($"Bar: {config.Bar.Position}, {config.Bar.Height}px; theme: Windows black / #0078D4");
        Console.WriteLine($"Terminal: {config.Expand("$term")}; launcher: {config.Expand("$menu")}");
        foreach (ConfigDiagnostic diagnostic in config.Diagnostics)
            Console.WriteLine($"{diagnostic.Severity}: {diagnostic.File}:{diagnostic.Line}: {diagnostic.Message}");
        foreach (string warning in InputHooks.ValidateBindings(config)) Console.WriteLine("Warning: " + warning);
        Console.WriteLine("Validation only. No hooks, processes, display changes or window movement were performed.");
        return config.Diagnostics.Any(item => item.Severity == DiagnosticSeverity.Error) ? 2 : 0;
    }

    private static void AttachOutput()
    {
        Native.AttachConsole(Native.AttachParentProcess);
        Console.SetOut(new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true });
        Console.SetError(new StreamWriter(Console.OpenStandardError(), new UTF8Encoding(false)) { AutoFlush = true });
    }

    private static void PrintHelp()
    {
        Console.WriteLine("SwayWin - hierarchical Windows tiling window manager");
        Console.WriteLine("SwayWin.exe [--config <Sway config file or directory>] [--overrides <windows.conf|none>]");
        Console.WriteLine("            [--no-autostart] [--no-bar] [--keep-titlebars]");
        Console.WriteLine("SwayWin.exe --check-config [<config path>]     Parse only, no desktop changes");
        Console.WriteLine("SwayWin.exe command \"workspace number 2\"     Send Sway-style command");
        Console.WriteLine("SwayWin.exe get_tree|get_workspaces|get_outputs|reload|exit");
        Console.WriteLine("SwayWin.exe --recover                         Restore a dead manager's journal");
        Console.WriteLine("SwayWin.exe --smoke-test                      Test only temporary own-process windows");
        Console.WriteLine("Exit and restore: tray menu or Ctrl+Alt+Shift+F12.");
        Console.WriteLine("Win+L stays Windows lock; use Win+Right for focus right.");
        Console.WriteLine("Windows overrides: Ctrl+Win+M microphone mute; Ctrl+Win+Up/Down laptop brightness.");
        Console.WriteLine("Linux display modes, secure-desktop handlers and arbitrary POSIX shell commands are not applied.");
        Console.WriteLine("Existing Windows wallpaper and application-owned content themes are preserved.");
    }
}

internal sealed class AppOptions
{
    internal string ConfigPath { get; private set; } = DefaultConfig;
    internal string? OverridesPath { get; private set; } = File.Exists(Path.Combine(AppContext.BaseDirectory, "windows.conf")) ? Path.Combine(AppContext.BaseDirectory, "windows.conf") : null;
    internal string? Verb { get; private set; }
    internal string Command { get; private set; } = "";
    internal bool NoAutostart { get; private set; }
    internal bool NoBar { get; private set; }
    internal bool KeepTitlebars { get; private set; }

    private static string DefaultConfig => File.Exists(@"M:\home\ex4zy\.config\sway\config")
        ? @"M:\home\ex4zy\.config\sway\config" : Path.Combine(AppContext.BaseDirectory, "config");

    internal static AppOptions Parse(string[] args)
    {
        AppOptions result = new();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--config": result.ConfigPath = Next(args, ref i); break;
                case "--overrides": string path = Next(args, ref i); result.OverridesPath = path == "none" ? null : path; break;
                case "--no-autostart": result.NoAutostart = true; break;
                case "--no-bar": result.NoBar = true; break;
                case "--keep-titlebars": result.KeepTitlebars = true; break;
                case "--help": case "-h": result.Verb = "help"; break;
                case "--recover": result.Verb = "recover"; break;
                case "--smoke-test": result.Verb = "smoke-test"; break;
                case "--check-config": case "check-config":
                    result.Verb = "check-config";
                    if (i + 1 < args.Length && !args[i + 1].StartsWith('-')) result.ConfigPath = args[++i];
                    break;
                case "get_tree": case "get_workspaces": case "get_outputs": case "reload": case "exit": result.Verb = args[i]; break;
                case "command":
                    result.Verb = "command"; result.Command = string.Join(' ', args.Skip(i + 1));
                    if (result.Command.Length == 0) throw new ArgumentException("command requires a Sway-style command string.");
                    return result;
                default: throw new ArgumentException($"Unknown option '{args[i]}'. Use --help.");
            }
        }
        result.ConfigPath = Path.GetFullPath(result.ConfigPath);
        if (result.OverridesPath is not null) result.OverridesPath = Path.GetFullPath(result.OverridesPath);
        return result;
    }

    private static string Next(string[] args, ref int index) => index + 1 < args.Length ? args[++index] : throw new ArgumentException($"{args[index]} requires a value.");
}

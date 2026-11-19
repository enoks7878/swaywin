using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SwayWin.Core.Configuration;

public sealed class SwayConfigLoader
{
    public SwayConfig Load(string path, string? overridesPath = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return new Import().Load(path, overridesPath);
    }

    private sealed class Import
    {
        private const int MaximumIncludeDepth = 64;
        private readonly SwayConfig config = new();
        private readonly HashSet<string> activeFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> sourceFiles = new(StringComparer.OrdinalIgnoreCase);
        private readonly List<Block> blocks = [];
        private readonly List<PendingBinding> bindings = [];
        private readonly List<Setting> settings = [];
        private readonly List<CriteriaDirective> criteriaDirectives = [];
        private readonly Dictionary<string, (string File, int Line)> variableSources = new(StringComparer.Ordinal);
        private string importedHome = "";
        private string importedRoot = "";
        private bool reportedColors;

        public SwayConfig Load(string path, string? overridesPath)
        {
            config.SourcePath = path;
            try
            {
                path = Path.GetFullPath(path);
                if (Directory.Exists(path))
                    path = Path.Combine(path, "config");
                config.SourcePath = path;
                InferImportedPaths(path);
                ReadFile(path, path, 0, 0);
                CloseUnfinishedBlocks();
                if (!string.IsNullOrWhiteSpace(overridesPath))
                {
                    var overrideFile = Path.GetFullPath(overridesPath);
                    if (Directory.Exists(overrideFile))
                        overrideFile = Path.Combine(overrideFile, "config");
                    ReadFile(overrideFile, overrideFile, 0, 0);
                    CloseUnfinishedBlocks();
                }
            }
            catch (Exception ex) when (IsFileException(ex))
            {
                Report(path, 0, $"Cannot open configuration: {ex.Message}", DiagnosticSeverity.Error);
            }
            FinalizeConfiguration();
            return config;
        }

        private void InferImportedPaths(string path)
        {
            importedRoot = Path.GetPathRoot(path) ?? "";
            var directory = Path.GetDirectoryName(path) ?? importedRoot;
            for (var current = new DirectoryInfo(directory); current is not null; current = current.Parent)
            {
                if (!current.Name.Equals("sway", StringComparison.OrdinalIgnoreCase) || current.Parent is null ||
                    !current.Parent.Name.Equals(".config", StringComparison.OrdinalIgnoreCase))
                    continue;
                importedHome = current.Parent.Parent?.FullName ?? directory;
                var homeDirectory = new DirectoryInfo(importedHome);
                // Also handle a copied Unix filesystem rooted in a normal Windows directory.
                if (homeDirectory.Parent?.Name.Equals("home", StringComparison.OrdinalIgnoreCase) == true)
                    importedRoot = homeDirectory.Parent.Parent?.FullName ?? importedRoot;
                else if (homeDirectory.Name == "root")
                    importedRoot = homeDirectory.Parent?.FullName ?? importedRoot;
                return;
            }
            importedHome = directory;
        }

        private string ResolveInclude(string value, string includingFile)
        {
            value = config.Expand(value);
            if (value == "~")
                return importedHome;
            if (value.StartsWith("~/", StringComparison.Ordinal) || value.StartsWith("~\\", StringComparison.Ordinal))
                return Path.GetFullPath(Path.Combine(importedHome, NativeSeparators(value[2..])));
            if (value.StartsWith('/'))
                return Path.GetFullPath(Path.Combine(importedRoot, NativeSeparators(value.TrimStart('/'))));
            value = NativeSeparators(value);
            return Path.GetFullPath(Path.IsPathRooted(value) ? value : Path.Combine(Path.GetDirectoryName(includingFile)!, value));
        }

        private static string NativeSeparators(string value) => value.Replace('/', Path.DirectorySeparatorChar);

        private void ReadFile(string path, string referringFile, int referringLine, int depth)
        {
            if (depth >= MaximumIncludeDepth)
            {
                Report(referringFile, referringLine, $"Include depth limit ({MaximumIncludeDepth}) reached at '{path}'.", DiagnosticSeverity.Error);
                return;
            }
            if (!activeFiles.Add(path))
            {
                Report(referringFile, referringLine, $"Include cycle detected at '{path}'.", DiagnosticSeverity.Error);
                return;
            }
            try
            {
                if (!File.Exists(path))
                {
                    Report(referringFile, referringLine, $"Configuration file not found: '{path}'.", DiagnosticSeverity.Error);
                    return;
                }
                if (sourceFiles.Add(path))
                    config.SourceFiles.Add(path);
                foreach (var (text, line) in LogicalLines(path))
                    ParseLine(text, path, line, depth);
            }
            catch (Exception ex) when (IsFileException(ex))
            {
                Report(referringFile, referringLine, $"Cannot read '{path}': {ex.Message}", DiagnosticSeverity.Error);
            }
            finally
            {
                activeFiles.Remove(path);
            }
        }

        private IEnumerable<(string Text, int Line)> LogicalLines(string file)
        {
            var pending = new StringBuilder();
            var start = 1;
            var line = 0;
            foreach (var physical in File.ReadLines(file))
            {
                line++;
                if (pending.Length == 0)
                    start = line;
                var text = SwaySyntax.StripComment(pending.ToString() + physical).TrimEnd();
                var slashes = 0;
                for (var i = text.Length - 1; i >= 0 && text[i] == '\\'; i--)
                    slashes++;
                if (slashes % 2 != 0)
                {
                    pending.Clear().Append(text.AsSpan(0, text.Length - 1)).Append(' ');
                    continue;
                }
                yield return (text, start);
                pending.Clear();
            }
            if (pending.Length > 0)
            {
                Report(file, start, "Continuation at end of file has no following line.", DiagnosticSeverity.Warning);
                yield return (pending.ToString(), start);
            }
        }

        private void ParseLine(string text, string file, int line, int depth)
        {
            if (!SwaySyntax.HasBalancedQuotes(text))
                Report(file, line, "Unterminated quoted string.", DiagnosticSeverity.Error);
            var pending = new StringBuilder();
            var quote = '\0';
            var brackets = 0;
            var literalBraces = 0;
            for (var i = 0; i < text.Length; i++)
            {
                var c = text[i];
                if (c == '\\' && i + 1 < text.Length && SwaySyntax.IsSyntaxEscape(text[i + 1]))
                {
                    pending.Append(c).Append(text[++i]);
                    continue;
                }
                if (c is '\'' or '"')
                {
                    if (quote == '\0')
                        quote = c;
                    else if (quote == c)
                        quote = '\0';
                    pending.Append(c);
                    continue;
                }
                if (quote == '\0')
                {
                    if (c == '[')
                        brackets++;
                    else if (c == ']' && brackets > 0)
                        brackets--;
                    if (brackets == 0 && c is '{' or '}' or ';')
                    {
                        var segment = pending.ToString().Trim();
                        if (c == '{' && literalBraces == 0 && CanOpenBlock(segment))
                        {
                            OpenBlock(segment, file, line);
                            pending.Clear();
                            continue;
                        }
                        if (c == '{')
                            literalBraces++;
                        else if (c == '}' && literalBraces > 0)
                            literalBraces--;
                        else if (c == '}')
                        {
                            ParseDirective(segment, file, line, depth);
                            pending.Clear();
                            if (blocks.Count == 0)
                                Report(file, line, "Unexpected closing '}'.", DiagnosticSeverity.Error);
                            else
                                blocks.RemoveAt(blocks.Count - 1);
                            continue;
                        }
                        else if (c == ';' && literalBraces == 0 && ShouldSplitConfiguration(segment, text[(i + 1)..]))
                        {
                            ParseDirective(segment, file, line, depth);
                            pending.Clear();
                            continue;
                        }
                    }
                }
                pending.Append(c);
            }
            ParseDirective(pending.ToString().Trim(), file, line, depth);
        }

        private static string DirectiveName(string text)
        {
            var position = 0;
            return SwaySyntax.ReadToken(text, ref position).ToLowerInvariant();
        }

        private static bool CanOpenBlock(string text)
        {
            var name = DirectiveName(text);
            return name.Length > 0 && name is not ("set" or "bindsym" or "bindcode" or "for_window" or "assign" or
                "no_focus" or "exec" or "exec_always" or "status_command" or "include") && !text.EndsWith('$');
        }

        private static bool ShouldSplitConfiguration(string segment, string remainder)
        {
            var name = DirectiveName(segment);
            if (name is not ("bindsym" or "bindcode" or "for_window" or "exec" or "exec_always" or "status_command" or "set"))
                return true;
            return (name is "bindsym" or "bindcode") && (DirectiveName(remainder) is "bindsym" or "bindcode" or "include");
        }

        private void OpenBlock(string header, string file, int line)
        {
            var position = 0;
            var name = SwaySyntax.ReadToken(header, ref position).ToLowerInvariant();
            var argument = header[position..].Trim();
            if (blocks.Any(block => block.Name is "ignored" or "colors"))
                name = "ignored";
            else
            {
                switch (name)
                {
                    case "mode":
                        if (argument.StartsWith("--pango_markup", StringComparison.Ordinal))
                            argument = argument["--pango_markup".Length..].Trim();
                        argument = SwaySyntax.Unquote(argument);
                        if (argument.Length == 0)
                            Report(file, line, "Mode block requires a name.", DiagnosticSeverity.Error);
                        break;
                    case "bar":
                        if (blocks.Any(block => block.Name == "bar"))
                            Report(file, line, "Nested bars are not supported by the Windows bar.", DiagnosticSeverity.Warning);
                        break;
                    case "colors":
                        ReportColors(file, line);
                        break;
                    case "input":
                        Report(file, line, $"Linux input selector '{config.Expand(argument)}' is not a Windows device selector; only keyboard repeat settings are imported.", DiagnosticSeverity.Info);
                        break;
                    case "output":
                        Report(file, line, $"Linux output '{config.Expand(argument)}' is not applied to Windows hardware; use windows_output mappings in overrides.", DiagnosticSeverity.Warning);
                        break;
                    case "seat":
                        Report(file, line, $"Linux seat '{config.Expand(argument)}' is not supported on Windows.", DiagnosticSeverity.Warning);
                        break;
                    default:
                        Report(file, line, $"Unknown configuration block '{name}'.", DiagnosticSeverity.Warning);
                        name = "ignored";
                        break;
                }
            }
            blocks.Add(new Block(name, argument, file, line));
        }

        private void CloseUnfinishedBlocks()
        {
            foreach (var block in blocks)
                Report(block.File, block.Line, $"Unclosed '{block.Name}' block.", DiagnosticSeverity.Error);
            blocks.Clear();
        }

        private void ParseDirective(string text, string file, int line, int depth)
        {
            if (text.Length == 0)
                return;
            var position = 0;
            var name = SwaySyntax.ReadToken(text, ref position).ToLowerInvariant();
            var arguments = text[position..].Trim();
            if (name == "include")
            {
                Include(arguments, file, line, depth);
                return;
            }
            if (blocks.Any(block => block.Name is "ignored" or "colors"))
                return;
            var device = blocks.LastOrDefault(block => block.Name is "input" or "seat" or "output");
            if (device is not null)
            {
                ParseDevice(device.Name, device.Argument, name, arguments, file, line);
                return;
            }
            if (blocks.Any(block => block.Name == "bar"))
            {
                if (name == "set")
                    SetVariable(arguments, file, line);
                else if (name == "bindsym" || name == "bindcode")
                    ParseBinding(name, arguments, file, line);
                else
                    settings.Add(new Setting(name, arguments, file, line, true));
                return;
            }

            switch (name)
            {
                case "set":
                    SetVariable(arguments, file, line);
                    break;
                case "bindsym":
                case "bindcode":
                    ParseBinding(name, arguments, file, line);
                    break;
                case "for_window":
                case "assign":
                case "no_focus":
                    criteriaDirectives.Add(new CriteriaDirective(name, arguments, file, line));
                    break;
                case "exec":
                case "exec_always":
                    ParseStartup(name, arguments, file, line);
                    break;
                case "input":
                case "seat":
                case "output":
                    var devicePosition = 0;
                    var selector = SwaySyntax.ReadToken(arguments, ref devicePosition);
                    var directive = SwaySyntax.ReadToken(arguments, ref devicePosition).ToLowerInvariant();
                    if (selector.Length == 0 || directive.Length == 0)
                        Report(file, line, $"'{name}' requires a selector and a directive or block.", DiagnosticSeverity.Error);
                    else
                        ParseDevice(name, selector, directive, arguments[devicePosition..].Trim(), file, line);
                    break;
                case "mode":
                    Report(file, line, "A configuration mode requires a block; a runtime mode command is not executed during import.", DiagnosticSeverity.Warning);
                    break;
                default:
                    if (name.StartsWith("client.", StringComparison.Ordinal))
                        ReportColors(file, line);
                    else
                        settings.Add(new Setting(name, arguments, file, line));
                    break;
            }
        }

        private void Include(string arguments, string file, int line, int depth)
        {
            var paths = SwaySyntax.Tokenize(arguments);
            if (paths.Count == 0)
            {
                Report(file, line, "Include requires a path.", DiagnosticSeverity.Error);
                return;
            }
            foreach (var value in paths)
            {
                try
                {
                    var path = ResolveInclude(value, file);
                    if (!HasWildcard(path))
                    {
                        if (IsBackup(path))
                            Report(file, line, $"Backup include is ignored: '{path}'.", DiagnosticSeverity.Info);
                        else
                            ReadFile(path, file, line, depth + 1);
                        continue;
                    }
                    var matches = GlobFiles(path).Where(candidate => !IsBackup(candidate)).Order(StringComparer.Ordinal).ToList();
                    if (matches.Count == 0)
                        Report(file, line, $"Optional include wildcard matched no files: '{path}'.", DiagnosticSeverity.Warning);
                    foreach (var match in matches)
                        ReadFile(match, file, line, depth + 1);
                }
                catch (Exception ex) when (IsFileException(ex) || ex is RegexMatchTimeoutException)
                {
                    Report(file, line, $"Cannot resolve include '{value}': {ex.Message}", HasWildcard(value) ? DiagnosticSeverity.Warning : DiagnosticSeverity.Error);
                }
            }
        }

        private static bool HasWildcard(string path) => path.IndexOfAny(['*', '?', '[']) >= 0;

        private static bool IsBackup(string path)
        {
            var name = Path.GetFileName(path);
            var extension = Path.GetExtension(name).ToLowerInvariant();
            return name.EndsWith('~') || name.StartsWith('#') || extension is ".bak" or ".backup" or ".old" or ".orig" or ".rej" or ".swp" or ".swo" or ".save";
        }

        private static IEnumerable<string> GlobFiles(string pattern)
        {
            var root = Path.GetPathRoot(pattern)!;
            var parts = pattern[root.Length..].Split([Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar], StringSplitOptions.RemoveEmptyEntries);
            IEnumerable<string> candidates = [root];
            for (var i = 0; i < parts.Length; i++)
            {
                var last = i == parts.Length - 1;
                var next = new List<string>();
                var part = parts[i];
                var matcher = HasWildcard(part) ? GlobPattern(part) : null;
                foreach (var directory in candidates)
                {
                    if (!Directory.Exists(directory))
                        continue;
                    if (matcher is null)
                    {
                        var path = Path.Combine(directory, part);
                        if (last ? File.Exists(path) : Directory.Exists(path))
                            next.Add(path);
                    }
                    else
                    {
                        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
                            if (matcher.IsMatch(Path.GetFileName(entry)) && (last ? File.Exists(entry) : Directory.Exists(entry)))
                                next.Add(entry);
                    }
                }
                candidates = next;
            }
            return candidates;
        }

        private static Regex GlobPattern(string pattern)
        {
            var regex = new StringBuilder("^");
            for (var i = 0; i < pattern.Length; i++)
            {
                switch (pattern[i])
                {
                    case '*': regex.Append(".*"); break;
                    case '?': regex.Append('.'); break;
                    case '[':
                        var end = pattern.IndexOf(']', i + 1);
                        if (end < 0)
                            regex.Append("\\[");
                        else
                        {
                            var content = pattern[(i + 1)..end];
                            regex.Append('[');
                            if (content.StartsWith('!'))
                            {
                                regex.Append('^');
                                content = content[1..];
                            }
                            regex.Append(content.Replace("\\", "\\\\", StringComparison.Ordinal)).Append(']');
                            i = end;
                        }
                        break;
                    default: regex.Append(Regex.Escape(pattern[i].ToString())); break;
                }
            }
            regex.Append('$');
            return new Regex(regex.ToString(), RegexOptions.CultureInvariant | RegexOptions.IgnoreCase, WindowCriteria.RegexTimeout);
        }

        private void SetVariable(string arguments, string file, int line)
        {
            var position = 0;
            var name = SwaySyntax.ReadToken(arguments, ref position);
            if (!name.StartsWith('$') || name.Length < 2)
            {
                Report(file, line, "Set requires a variable name beginning with '$'.", DiagnosticSeverity.Error);
                return;
            }
            if (name.Skip(1).Any(c => !SwayConfig.IsVariableCharacter(c)))
            {
                Report(file, line, $"Invalid variable name '{name}'.", DiagnosticSeverity.Error);
                return;
            }
            config.Variables[name] = SwaySyntax.Unquote(arguments[position..].Trim());
            variableSources[name] = (file, line);
        }

        private void ParseBinding(string name, string arguments, string file, int line)
        {
            var position = 0;
            var toCode = name == "bindcode";
            var release = false;
            var noRepeat = false;
            var locked = false;
            var keys = "";
            while (position < arguments.Length)
            {
                var token = SwaySyntax.ReadToken(arguments, ref position);
                if (!token.StartsWith("--", StringComparison.Ordinal))
                {
                    keys = token;
                    break;
                }
                switch (token)
                {
                    case "--to-code": toCode = true; break;
                    case "--release": release = true; break;
                    case "--no-repeat": noRepeat = true; break;
                    case "--locked": locked = true; break;
                    default: Report(file, line, $"Binding option '{token}' is not supported on Windows.", DiagnosticSeverity.Warning); break;
                }
            }
            var command = arguments[position..].Trim();
            if (keys.Length == 0 || command.Length == 0)
            {
                Report(file, line, "Binding requires keys and a command.", DiagnosticSeverity.Error);
                return;
            }
            var mode = blocks.LastOrDefault(block => block.Name == "mode")?.Argument ?? "default";
            bindings.Add(new PendingBinding(new KeyBinding(mode, keys, command, toCode, release, noRepeat, file, line, locked), name == "bindcode"));
        }

        private void ParseStartup(string name, string arguments, string file, int line)
        {
            if (arguments.StartsWith("--no-startup-id", StringComparison.Ordinal))
                arguments = arguments["--no-startup-id".Length..].Trim();
            if (arguments.Length == 0)
            {
                Report(file, line, "Startup directive requires a command.", DiagnosticSeverity.Error);
                return;
            }
            config.StartupCommands.Add(arguments);
            if (name == "exec_always")
                config.ReloadCommands.Add(arguments);
            config.StartupEntries.Add(new StartupCommand(arguments, name == "exec_always", file, line));
            var expanded = config.Expand(arguments);
            if (expanded.Contains("swaybg", StringComparison.Ordinal) || expanded.Contains("swayidle", StringComparison.Ordinal))
                Report(file, line, "Linux wallpaper/session startup command is retained for App translation and is not executed by the loader.", DiagnosticSeverity.Warning);
            else
                Report(file, line, "Startup command is retained only; the loader does not execute it.", DiagnosticSeverity.Info);
        }

        private void ParseDevice(string device, string selector, string name, string arguments, string file, int line)
        {
            if (device == "input" && (name is "repeat_rate" or "repeat_delay"))
            {
                settings.Add(new Setting(name, arguments, file, line));
                return;
            }
            var message = device switch
            {
                "input" when name.StartsWith("xkb_", StringComparison.Ordinal) =>
                    $"Linux keyboard directive '{name} {config.Expand(arguments)}' is not applied; Windows-native keyboard languages and OS settings are unchanged.",
                "input" => $"Linux input directive '{selector} {name} {config.Expand(arguments)}' is not supported on Windows.",
                "seat" => $"Linux seat directive '{selector} {name} {config.Expand(arguments)}' is not supported on Windows.",
                _ => $"Linux output directive '{selector} {name} {config.Expand(arguments)}' is not applied to Windows hardware; map aliases with windows_output."
            };
            Report(file, line, message, DiagnosticSeverity.Warning);
        }

        private void FinalizeConfiguration()
        {
            ValidateVariables();
            foreach (var setting in settings)
                ApplySetting(setting);
            foreach (var pending in bindings)
            {
                var binding = pending.Binding with { Mode = config.Expand(pending.Binding.Mode), Keys = config.Expand(pending.Binding.Keys) };
                if (pending.IsCode)
                {
                    var parts = binding.Keys.Split('+');
                    if (!int.TryParse(parts[^1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var code) || !LinuxKeyCodes.TryGetValue(code, out var key))
                    {
                        Report(binding.SourcePath, binding.LineNumber, $"Linux bindcode '{binding.Keys}' cannot be mapped to a known Windows key; binding is skipped.", DiagnosticSeverity.Warning);
                        continue;
                    }
                    parts[^1] = key;
                    binding = binding with { Keys = string.Join('+', parts) };
                    Report(binding.SourcePath, binding.LineNumber, $"Linux XKB keycode {code} is mapped to '{key}' for Windows physical-key handling.", DiagnosticSeverity.Info);
                }
                if (binding.Keys.Contains('$'))
                    Report(binding.SourcePath, binding.LineNumber, $"Binding keys contain an unresolved variable: '{binding.Keys}'.", DiagnosticSeverity.Warning);
                var previous = config.Bindings.FindIndex(existing => existing.Mode == binding.Mode && existing.Keys.Equals(binding.Keys, StringComparison.OrdinalIgnoreCase) &&
                    existing.Release == binding.Release && existing.ToCode == binding.ToCode);
                if (previous >= 0)
                    config.Bindings.RemoveAt(previous);
                config.Bindings.Add(binding);
            }
            foreach (var directive in criteriaDirectives)
                ApplyCriteria(directive);
        }

        private void ValidateVariables()
        {
            var visiting = new HashSet<string>(StringComparer.Ordinal);
            var done = new HashSet<string>(StringComparer.Ordinal);
            foreach (var name in config.Variables.Keys)
                Visit(name, 0);

            void Visit(string name, int depth)
            {
                if (done.Contains(name))
                    return;
                if (depth >= 32 || !visiting.Add(name))
                {
                    var source = variableSources[name];
                    Report(source.File, source.Line, $"Cyclic or excessively deep variable expansion at '{name}'.", DiagnosticSeverity.Warning);
                    return;
                }
                var value = config.Variables[name];
                for (var i = 0; i < value.Length; i++)
                {
                    if (value[i] != '$')
                        continue;
                    var end = i + 1;
                    if (end < value.Length && value[end] == '{')
                        end++;
                    var start = end;
                    while (end < value.Length && SwayConfig.IsVariableCharacter(value[end]))
                        end++;
                    var dependency = "$" + value[start..end];
                    if (config.Variables.ContainsKey(dependency))
                        Visit(dependency, depth + 1);
                    i = end - 1;
                }
                visiting.Remove(name);
                done.Add(name);
            }
        }

        private void ApplyCriteria(CriteriaDirective directive)
        {
            // Criteria and destinations are configuration values; rule commands remain raw until execution.
            var extracted = SwaySyntax.ExtractCriteria(config.Expand(directive.Arguments));
            if (extracted.Criteria is null)
            {
                Report(directive.File, directive.Line, $"'{directive.Name}' requires bracketed window criteria.", DiagnosticSeverity.Error);
                return;
            }
            foreach (var error in extracted.Criteria.ValidationErrors)
                Report(directive.File, directive.Line, error, DiagnosticSeverity.Warning);
            switch (directive.Name)
            {
                case "for_window":
                    var raw = SwaySyntax.ExtractCriteria(directive.Arguments);
                    if (extracted.Command.Length == 0)
                        Report(directive.File, directive.Line, "for_window requires a command.", DiagnosticSeverity.Error);
                    else
                        config.WindowRules.Add(new WindowRule(extracted.Criteria, raw.Command, directive.File, directive.Line));
                    break;
                case "no_focus":
                    if (extracted.Command.Length > 0)
                        Report(directive.File, directive.Line, "Unexpected command after no_focus criteria.", DiagnosticSeverity.Warning);
                    config.NoFocusRules.Add(extracted.Criteria);
                    break;
                case "assign":
                    var tokens = SwaySyntax.Tokenize(extracted.Command);
                    if (tokens.Count > 0 && tokens[0] is "->" or "\u2192")
                        tokens.RemoveAt(0);
                    if (tokens.Count > 0 && tokens[0] == "output")
                    {
                        Report(directive.File, directive.Line, "assign to output is not a workspace assignment; use workspace output and windows_output mappings.", DiagnosticSeverity.Warning);
                        break;
                    }
                    if (tokens.Count > 0 && tokens[0] == "workspace")
                        tokens.RemoveAt(0);
                    if (tokens.Count > 0 && tokens[0] == "number")
                        tokens.RemoveAt(0);
                    if (tokens.Count == 0)
                        Report(directive.File, directive.Line, "assign requires a workspace.", DiagnosticSeverity.Error);
                    else
                        config.Assignments.Add(new AssignmentRule(extracted.Criteria, string.Join(' ', tokens)));
                    break;
            }
        }

        private void ApplySetting(Setting setting)
        {
            var arguments = config.Expand(setting.Arguments);
            var tokens = SwaySyntax.Tokenize(arguments);
            var value = SwaySyntax.Unquote(arguments);
            var s = config.Settings;
            if (setting.Bar)
            {
                ApplyBarSetting(setting, value);
                return;
            }
            switch (setting.Name)
            {
                case "gaps":
                    if (tokens.Count != 2 || !Integer(tokens[1], out var gap))
                        Invalid(setting, "Expected gaps <inner|outer|top|bottom|left|right|horizontal|vertical> <integer>.");
                    else
                    {
                        switch (tokens[0])
                        {
                            case "inner": s.GapInner = gap; break;
                            case "outer": s.GapOuter = gap; s.GapTop = s.GapBottom = s.GapLeft = s.GapRight = null; break;
                            case "top": s.GapTop = gap; break;
                            case "bottom": s.GapBottom = gap; break;
                            case "left": s.GapLeft = gap; break;
                            case "right": s.GapRight = gap; break;
                            case "horizontal": s.GapLeft = s.GapRight = gap; break;
                            case "vertical": s.GapTop = s.GapBottom = gap; break;
                            default: Invalid(setting, $"Unsupported gap direction '{tokens[0]}'."); break;
                        }
                    }
                    break;
                case "smart_gaps":
                    if (value is "on" or "off" or "inverse") s.SmartGaps = value;
                    else Invalid(setting, "smart_gaps expects on, off, or inverse.");
                    break;
                case "smart_borders":
                    if (value == "no_gaps") s.SmartBorders = true;
                    else if (WindowCriteria.TryBoolean(value, out var smart)) s.SmartBorders = smart;
                    else Invalid(setting, "smart_borders expects on, off, or no_gaps.");
                    break;
                case "hide_edge_borders":
                    if (value is "none" or "vertical" or "horizontal" or "both" or "smart" or "smart_no_gaps") s.HideEdgeBorders = value;
                    else Invalid(setting, "Invalid hide_edge_borders value.");
                    break;
                case "default_border":
                case "default_floating_border":
                    if (tokens.Count is < 1 or > 2 || tokens[0] is not ("pixel" or "normal" or "none") ||
                        (tokens.Count == 2 && (!Integer(tokens[1], out var borderSize) || borderSize < 0)))
                        Invalid(setting, "Border expects pixel, normal, or none and an optional nonnegative width.");
                    else if (setting.Name == "default_border")
                    {
                        s.DefaultBorder = tokens[0];
                        if (tokens.Count == 2) s.BorderWidth = int.Parse(tokens[1], CultureInfo.InvariantCulture);
                    }
                    else
                    {
                        s.DefaultFloatingBorder = tokens[0];
                        if (tokens.Count == 2) s.FloatingBorderWidth = int.Parse(tokens[1], CultureInfo.InvariantCulture);
                    }
                    break;
                case "focus_follows_mouse":
                    if (value == "always") s.FocusFollowsMouse = true;
                    else if (WindowCriteria.TryBoolean(value, out var follows)) s.FocusFollowsMouse = follows;
                    else Invalid(setting, "focus_follows_mouse expects yes, no, or always.");
                    break;
                case "mouse_warping":
                    if (value is "output" or "container") s.MouseWarping = true;
                    else if (value == "none") s.MouseWarping = false;
                    else if (WindowCriteria.TryBoolean(value, out var warping)) s.MouseWarping = warping;
                    else Invalid(setting, "mouse_warping expects output, container, or none.");
                    break;
                case "floating_modifier":
                    if (tokens.Count == 0) Invalid(setting, "floating_modifier requires a modifier.");
                    else
                    {
                        s.FloatingModifier = tokens[0];
                        if (tokens.Count > 1 && tokens[1] == "inverse")
                            Report(setting.File, setting.Line, "Inverse Linux floating mouse-button mapping is not applied on Windows.", DiagnosticSeverity.Warning);
                    }
                    break;
                case "floating_minimum_size":
                case "floating_maximum_size":
                    var size = value.Split(['x', 'X'], StringSplitOptions.TrimEntries);
                    if (size.Length != 2 || !Integer(size[0], out var width) || !Integer(size[1], out var height) || width < -1 || height < -1)
                        Invalid(setting, "Floating size expects <width> x <height>; -1 means unlimited.");
                    else if (setting.Name == "floating_minimum_size")
                    {
                        s.MinimumFloatingWidth = Math.Max(0, width);
                        s.MinimumFloatingHeight = Math.Max(0, height);
                    }
                    else
                    {
                        s.MaximumFloatingWidth = Math.Max(0, width);
                        s.MaximumFloatingHeight = Math.Max(0, height);
                    }
                    break;
                case "workspace_layout":
                    if (value is "default" or "splith" or "splitv" or "tabbed" or "stacking" or "stacked") s.WorkspaceLayout = value switch { "default" => "splith", "stacked" => "stacking", _ => value };
                    else Invalid(setting, "Invalid workspace_layout value.");
                    break;
                case "workspace_auto_back_and_forth":
                    if (WindowCriteria.TryBoolean(value, out var backAndForth)) s.WorkspaceAutoBackAndForth = backAndForth;
                    else Invalid(setting, "workspace_auto_back_and_forth expects yes or no.");
                    break;
                case "repeat_rate":
                case "repeat_delay":
                    if (!Integer(value, out var repeat) || repeat < 0) Invalid(setting, "Keyboard repeat value must be nonnegative.");
                    else if (setting.Name == "repeat_rate") s.RepeatRate = repeat;
                    else s.RepeatDelay = repeat;
                    break;
                case "workspace":
                    var output = tokens.IndexOf("output");
                    var workspaceStart = tokens.Count > 0 && tokens[0] == "number" ? 1 : 0;
                    if (output <= workspaceStart || output + 1 >= tokens.Count) Invalid(setting, "Expected workspace <name> output <sway-output>.");
                    else
                    {
                        config.WorkspaceOutputs[string.Join(' ', tokens.Skip(workspaceStart).Take(output - workspaceStart))] = tokens[output + 1];
                        if (output + 2 < tokens.Count)
                            Report(setting.File, setting.Line, "Only the first preferred workspace output is mapped on Windows.", DiagnosticSeverity.Warning);
                    }
                    break;
                case "windows_output":
                    if (tokens.Count != 2) Invalid(setting, "Expected windows_output <sway-name> <Windows-display-name>.");
                    else config.OutputMappings[tokens[0]] = tokens[1];
                    break;
                case "font":
                    Report(setting.File, setting.Line, "Pango font configuration is not applied; Windows renders with its native black-blue theme.", DiagnosticSeverity.Info);
                    break;
                case "background":
                case "wallpaper":
                case "status_command":
                    Report(setting.File, setting.Line, $"Linux '{setting.Name}' is not executed or applied by the loader.", DiagnosticSeverity.Warning);
                    break;
                default:
                    Report(setting.File, setting.Line, $"Unknown configuration directive '{setting.Name}'.", DiagnosticSeverity.Warning);
                    break;
            }
        }

        private void ApplyBarSetting(Setting setting, string value)
        {
            var bar = config.Bar;
            switch (setting.Name)
            {
                case "position":
                    if (value is "top" or "bottom") bar.Position = value;
                    else Invalid(setting, "Bar position expects top or bottom.");
                    break;
                case "height":
                    if (Integer(value, out var height) && height > 0) bar.Height = height;
                    else Invalid(setting, "Bar height must be positive.");
                    break;
                case "mode":
                    if (value is "dock" or "hide" or "invisible") { bar.Mode = value; bar.Enabled = value != "invisible"; }
                    else Invalid(setting, "Bar mode expects dock, hide, or invisible.");
                    break;
                case "enabled":
                    if (WindowCriteria.TryBoolean(value, out var enabled)) bar.Enabled = enabled;
                    else Invalid(setting, "Bar enabled expects yes or no.");
                    break;
                case "status_command":
                    bar.StatusCommand = setting.Arguments;
                    Report(setting.File, setting.Line, "Linux status_command is retained for native App translation; no shell/status process is started.", DiagnosticSeverity.Warning);
                    break;
                case "font": bar.Font = value; break;
                case "separator_symbol": bar.SeparatorSymbol = value; break;
                case "workspace_min_width":
                    if (Integer(value, out var minimum) && minimum >= 0) bar.WorkspaceMinWidth = minimum;
                    else Invalid(setting, "workspace_min_width must be nonnegative.");
                    break;
                case "strip_workspace_numbers":
                    if (WindowCriteria.TryBoolean(value, out var strip)) bar.StripWorkspaceNumbers = strip;
                    else Invalid(setting, "strip_workspace_numbers expects yes or no.");
                    break;
                case "workspace_buttons":
                case "binding_mode_indicator":
                case "strip_workspace_name":
                case "separator":
                case "hidden_state":
                case "modifier":
                case "id":
                case "pango_markup":
                    Report(setting.File, setting.Line, $"Bar presentation option '{setting.Name}' uses Windows-native presentation.", DiagnosticSeverity.Info);
                    break;
                case "colors": ReportColors(setting.File, setting.Line); break;
                case "output":
                case "tray_output":
                case "tray_padding":
                case "swaybar_command":
                case "i3bar_command":
                case "icon_theme":
                    Report(setting.File, setting.Line, $"Linux bar option '{setting.Name}' is not applied on Windows.", DiagnosticSeverity.Warning);
                    break;
                default:
                    Report(setting.File, setting.Line, $"Unknown bar directive '{setting.Name}'.", DiagnosticSeverity.Warning);
                    break;
            }
        }

        private void ReportColors(string file, int line)
        {
            if (reportedColors)
                return;
            reportedColors = true;
            Report(file, line, "Sway colors/client.* are ignored in favor of the Windows black-blue theme.", DiagnosticSeverity.Info);
        }

        private void Invalid(Setting setting, string message) => Report(setting.File, setting.Line, message, DiagnosticSeverity.Error);
        private void Report(string file, int line, string message, DiagnosticSeverity severity) => config.Diagnostics.Add(new ConfigDiagnostic(file, line, message, severity));
        private static bool Integer(string value, out int result) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out result);
        private static bool IsFileException(Exception ex) => ex is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException or System.Security.SecurityException;

        private sealed record Block(string Name, string Argument, string File, int Line);
        private sealed record PendingBinding(KeyBinding Binding, bool IsCode);
        private sealed record Setting(string Name, string Arguments, string File, int Line, bool Bar = false);
        private sealed record CriteriaDirective(string Name, string Arguments, string File, int Line);

        private static readonly Dictionary<int, string> LinuxKeyCodes = new()
        {
            [9] = "Escape", [10] = "1", [11] = "2", [12] = "3", [13] = "4", [14] = "5", [15] = "6", [16] = "7", [17] = "8", [18] = "9", [19] = "0",
            [20] = "minus", [21] = "equal", [22] = "BackSpace", [23] = "Tab", [24] = "q", [25] = "w", [26] = "e", [27] = "r", [28] = "t", [29] = "y", [30] = "u", [31] = "i", [32] = "o", [33] = "p",
            [34] = "bracketleft", [35] = "bracketright", [36] = "Return", [37] = "Control_L", [38] = "a", [39] = "s", [40] = "d", [41] = "f", [42] = "g", [43] = "h", [44] = "j", [45] = "k", [46] = "l",
            [47] = "semicolon", [48] = "apostrophe", [49] = "grave", [50] = "Shift_L", [51] = "backslash", [52] = "z", [53] = "x", [54] = "c", [55] = "v", [56] = "b", [57] = "n", [58] = "m",
            [59] = "comma", [60] = "period", [61] = "slash", [62] = "Shift_R", [63] = "KP_Multiply", [64] = "Alt_L", [65] = "space", [66] = "Caps_Lock",
            [67] = "F1", [68] = "F2", [69] = "F3", [70] = "F4", [71] = "F5", [72] = "F6", [73] = "F7", [74] = "F8", [75] = "F9", [76] = "F10", [95] = "F11", [96] = "F12",
            [77] = "Num_Lock", [78] = "Scroll_Lock", [79] = "KP_Home", [80] = "KP_Up", [81] = "KP_Prior", [82] = "KP_Subtract", [83] = "KP_Left", [84] = "KP_Begin", [85] = "KP_Right", [86] = "KP_Add",
            [87] = "KP_End", [88] = "KP_Down", [89] = "KP_Next", [90] = "KP_Insert", [91] = "KP_Delete", [104] = "KP_Enter", [105] = "Control_R", [106] = "KP_Divide", [107] = "Print", [108] = "Alt_R",
            [110] = "Home", [111] = "Up", [112] = "Prior", [113] = "Left", [114] = "Right", [115] = "End", [116] = "Down", [117] = "Next", [118] = "Insert", [119] = "Delete", [127] = "Pause",
            [133] = "Super_L", [134] = "Super_R", [135] = "Menu", [121] = "XF86AudioMute", [122] = "XF86AudioLowerVolume", [123] = "XF86AudioRaiseVolume", [171] = "XF86AudioNext", [172] = "XF86AudioPlay", [173] = "XF86AudioPrev", [174] = "XF86AudioStop",
            [232] = "XF86MonBrightnessDown", [233] = "XF86MonBrightnessUp"
        };
    }
}

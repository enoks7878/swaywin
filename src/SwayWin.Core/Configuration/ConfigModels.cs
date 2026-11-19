using System.Collections.ObjectModel;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SwayWin.Core.Configuration;

public enum DiagnosticSeverity
{
    Info,
    Warning,
    Error
}

public sealed record ConfigDiagnostic(string File, int Line, string Message, DiagnosticSeverity Severity);

public sealed class SwayConfig
{
    public string SourcePath { get; set; } = "";
    public List<string> SourceFiles { get; } = [];
    public List<ConfigDiagnostic> Diagnostics { get; } = [];
    public Dictionary<string, string> Variables { get; } = new(StringComparer.Ordinal);
    public List<KeyBinding> Bindings { get; } = [];
    public List<string> StartupCommands { get; } = [];
    public List<string> ReloadCommands { get; } = [];
    public List<StartupCommand> StartupEntries { get; } = [];
    public List<WindowRule> WindowRules { get; } = [];
    public List<AssignmentRule> Assignments { get; } = [];
    public List<WindowCriteria> NoFocusRules { get; } = [];
    public Dictionary<string, string> WorkspaceOutputs { get; } = new(StringComparer.Ordinal);
    public Dictionary<string, string> OutputMappings { get; } = new(StringComparer.Ordinal);
    public WindowSettings Settings { get; set; } = new();
    public BarSettings Bar { get; set; } = new();

    public string Expand(string value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return Expand(value, new HashSet<string>(StringComparer.Ordinal), 0);
    }

    private string Expand(string value, HashSet<string> active, int depth)
    {
        if (depth >= 32 || value.Length > 1_048_576)
            return value;

        var result = new StringBuilder();
        for (var i = 0; i < value.Length; i++)
        {
            if (value[i] != '$')
            {
                result.Append(value[i]);
                continue;
            }

            var end = i + 1;
            var braced = end < value.Length && value[end] == '{';
            if (braced)
                end++;
            var nameStart = end;
            while (end < value.Length && IsVariableCharacter(value[end]))
                end++;
            if (end == nameStart || (braced && (end >= value.Length || value[end] != '}')))
            {
                result.Append('$');
                continue;
            }

            var name = "$" + value[nameStart..end];
            if (braced)
                end++;
            var original = value[i..end];
            if (Variables.TryGetValue(name, out var replacement) && active.Add(name))
            {
                result.Append(Expand(replacement, active, depth + 1));
                active.Remove(name);
            }
            else
                result.Append(original);
            i = end - 1;

            // Keep cyclic or exponentially growing variable definitions bounded.
            if (result.Length > 1_048_576)
                return value;
        }
        return result.ToString();
    }

    internal static bool IsVariableCharacter(char c) => char.IsLetterOrDigit(c) || c is '_' or '-';
}

public sealed record KeyBinding(string Mode, string Keys, string Command, bool ToCode = false,
    bool Release = false, bool NoRepeat = false, string SourcePath = "", int LineNumber = 0, bool Locked = false);

public sealed record StartupCommand(string Command, bool Always = false, string SourcePath = "", int LineNumber = 0);

public sealed class WindowSettings
{
    public int GapInner { get; set; } = 8;
    public int GapOuter { get; set; } = 8;
    public int? GapTop { get; set; }
    public int? GapBottom { get; set; }
    public int? GapLeft { get; set; }
    public int? GapRight { get; set; }
    public string SmartGaps { get; set; } = "off";
    public bool SmartBorders { get; set; } = true;
    public string HideEdgeBorders { get; set; } = "none";
    public string DefaultBorder { get; set; } = "pixel";
    public int BorderWidth { get; set; } = 2;
    public string DefaultFloatingBorder { get; set; } = "normal";
    public int FloatingBorderWidth { get; set; } = 2;
    public bool FocusFollowsMouse { get; set; } = true;
    public bool MouseWarping { get; set; } = true;
    public string FloatingModifier { get; set; } = "Mod4";
    public string WorkspaceLayout { get; set; } = "splith";
    public bool WorkspaceAutoBackAndForth { get; set; }
    public int MinimumFloatingWidth { get; set; } = 75;
    public int MinimumFloatingHeight { get; set; } = 50;
    public int MaximumFloatingWidth { get; set; }
    public int MaximumFloatingHeight { get; set; }
    public int RepeatRate { get; set; } = 25;
    public int RepeatDelay { get; set; } = 600;
}

public sealed class BarSettings
{
    public bool Enabled { get; set; } = true;
    public string Position { get; set; } = "bottom";
    public int Height { get; set; } = 28;
    public string Mode { get; set; } = "dock";
    public string StatusCommand { get; set; } = "";
    public string Font { get; set; } = "";
    public string SeparatorSymbol { get; set; } = " | ";
    public int WorkspaceMinWidth { get; set; }
    public bool StripWorkspaceNumbers { get; set; }
}

public sealed record WindowIdentity(long Id, string Title, string Class, string ProcessName,
    bool Floating = false, string Workspace = "", bool Urgent = false);

public sealed class WindowCriteria
{
    public static TimeSpan RegexTimeout { get; } = TimeSpan.FromMilliseconds(50);

    public IReadOnlyDictionary<string, string> Fields { get; }
    public string Raw { get; }
    public IReadOnlyList<string> ValidationErrors { get; }
    public bool IsValid => ValidationErrors.Count == 0;

    private readonly Dictionary<string, Regex> patterns = new(StringComparer.Ordinal);

    private WindowCriteria(string raw, Dictionary<string, string> fields, List<string> errors)
    {
        Raw = raw;
        Fields = new ReadOnlyDictionary<string, string>(fields);
        foreach (var (field, value) in fields)
        {
            switch (field)
            {
                case "app_id":
                case "instance":
                case "class":
                case "title":
                case "workspace":
                    if (value == "__focused__")
                        break;
                    try
                    {
                        var options = RegexOptions.CultureInvariant;
                        if (field is "app_id" or "instance")
                            options |= RegexOptions.IgnoreCase;
                        patterns[field] = new Regex(value, options, RegexTimeout);
                    }
                    catch (ArgumentException ex)
                    {
                        errors.Add($"Invalid regex for '{field}': {ex.Message}");
                    }
                    break;
                case "con_id":
                case "id":
                    if (value != "__focused__" && !long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out _))
                        errors.Add($"Invalid window ID '{value}'.");
                    break;
                case "floating":
                case "tiling":
                case "urgent":
                    if (!TryBoolean(value, out _))
                        errors.Add($"Unsupported value '{value}' for criterion '{field}'.");
                    break;
                default:
                    errors.Add($"Unsupported window criterion '{field}'.");
                    break;
            }
        }
        ValidationErrors = errors.AsReadOnly();
    }

    public static WindowCriteria Parse(string raw)
    {
        ArgumentNullException.ThrowIfNull(raw);
        var text = raw.Trim();
        var errors = new List<string>();
        if (text.StartsWith('['))
        {
            if (text.EndsWith(']'))
                text = text[1..^1];
            else
            {
                errors.Add("Window criteria are missing a closing ']'.");
                text = text[1..];
            }
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;
            if (i == text.Length)
                break;
            var start = i;
            while (i < text.Length && !char.IsWhiteSpace(text[i]) && text[i] != '=')
                i++;
            var field = text[start..i];
            if (field.Length == 0)
            {
                errors.Add("Window criterion is missing a field name.");
                i++;
                continue;
            }
            while (i < text.Length && char.IsWhiteSpace(text[i]))
                i++;

            var value = "true";
            if (i < text.Length && text[i] == '=')
            {
                i++;
                while (i < text.Length && char.IsWhiteSpace(text[i]))
                    i++;
                if (i == text.Length)
                    errors.Add($"Window criterion '{field}' is missing a value.");
                var valueStart = i;
                value = SwaySyntax.ReadToken(text, ref i);
                if (!SwaySyntax.HasBalancedQuotes(text[valueStart..i]))
                    errors.Add($"Unterminated quote in criterion '{field}'.");
            }
            else if (field is not ("floating" or "tiling" or "urgent"))
                errors.Add($"Window criterion '{field}' requires a value.");

            if (!fields.TryAdd(field, value))
                errors.Add($"Window criterion '{field}' is repeated.");
        }
        return new WindowCriteria(raw, fields, errors);
    }

    public bool Matches(WindowIdentity identity, WindowIdentity? focused = null)
    {
        ArgumentNullException.ThrowIfNull(identity);
        if (!IsValid)
            return false;
        try
        {
            foreach (var (field, value) in Fields)
            {
                switch (field)
                {
                    case "con_id":
                    case "id":
                        if (value == "__focused__")
                        {
                            if (focused is null || identity.Id != focused.Id)
                                return false;
                        }
                        else if (identity.Id != long.Parse(value, CultureInfo.InvariantCulture))
                            return false;
                        break;
                    case "floating":
                    case "tiling":
                    case "urgent":
                        TryBoolean(value, out var expected);
                        var actual = field == "urgent" ? identity.Urgent : field == "floating" ? identity.Floating : !identity.Floating;
                        if (actual != expected)
                            return false;
                        break;
                    default:
                        var candidate = IdentityField(identity, field);
                        if (value == "__focused__")
                        {
                            var comparison = field is "app_id" or "instance" ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
                            if (focused is null || !string.Equals(candidate, IdentityField(focused, field), comparison))
                                return false;
                        }
                        else if (field is "app_id" or "instance")
                        {
                            var withoutExtension = candidate.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? candidate[..^4] : candidate;
                            if (!patterns[field].IsMatch(candidate) && !patterns[field].IsMatch(withoutExtension) && !patterns[field].IsMatch(withoutExtension + ".exe"))
                                return false;
                        }
                        else if (!patterns[field].IsMatch(candidate))
                            return false;
                        break;
                }
            }
            return true;
        }
        catch (RegexMatchTimeoutException)
        {
            return false;
        }
    }

    private static string IdentityField(WindowIdentity identity, string field) => field switch
    {
        "app_id" or "instance" => identity.ProcessName,
        "class" => identity.Class,
        "title" => identity.Title,
        "workspace" => identity.Workspace,
        _ => ""
    };

    internal static bool TryBoolean(string value, out bool result)
    {
        switch (value.ToLowerInvariant())
        {
            case "yes": case "on": case "true": case "1":
                result = true;
                return true;
            case "no": case "off": case "false": case "0":
                result = false;
                return true;
            default:
                result = false;
                return false;
        }
    }
}

public sealed record WindowRule(WindowCriteria Criteria, string Command, string SourcePath = "", int LineNumber = 0);
public sealed record AssignmentRule(WindowCriteria Criteria, string Workspace);

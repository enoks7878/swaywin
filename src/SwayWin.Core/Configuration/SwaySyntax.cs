using System.Text;

namespace SwayWin.Core.Configuration;

public static class SwaySyntax
{
    public static List<string> Tokenize(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = new List<string>();
        var position = 0;
        while (position < text.Length)
        {
            while (position < text.Length && char.IsWhiteSpace(text[position]))
                position++;
            if (position < text.Length)
                tokens.Add(ReadToken(text, ref position));
        }
        return tokens;
    }

    internal static string ReadToken(string text, ref int position)
    {
        while (position < text.Length && char.IsWhiteSpace(text[position]))
            position++;
        var result = new StringBuilder();
        var quote = '\0';
        while (position < text.Length)
        {
            var c = text[position];
            if (quote == '\0' && char.IsWhiteSpace(c))
                break;
            position++;
            if (c == '\\' && position < text.Length)
            {
                var next = text[position];
                // Only syntax escapes are decoded. In particular, C:\Tools and UNC paths stay intact.
                if (next == '\\')
                {
                    result.Append(c).Append(next);
                    position++;
                    continue;
                }
                if ((quote == '\0' && (char.IsWhiteSpace(next) || next is '\'' or '"' or ';' or ',' or '#' or '{' or '}' or '[' or ']')) || next == quote)
                {
                    result.Append(next);
                    position++;
                    continue;
                }
                result.Append(c);
                continue;
            }
            if (c is '\'' or '"')
            {
                if (quote == '\0')
                    quote = c;
                else if (quote == c)
                    quote = '\0';
                else
                    result.Append(c);
            }
            else
                result.Append(c);
        }
        return result.ToString();
    }

    public static IReadOnlyList<string> SplitCommands(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var commands = new List<string>();
        var quote = '\0';
        var brackets = 0;
        var start = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && IsSyntaxEscape(text[i + 1]))
            {
                i++;
                continue;
            }
            if (c is '\'' or '"')
            {
                if (quote == '\0')
                    quote = c;
                else if (quote == c)
                    quote = '\0';
                continue;
            }
            if (quote != '\0')
                continue;
            if (c == '[')
                brackets++;
            else if (c == ']' && brackets > 0)
                brackets--;
            else if (brackets == 0 && c is ';' or ',')
            {
                AddCommand(commands, text[start..i]);
                start = i + 1;
            }
        }
        AddCommand(commands, text[start..]);
        return commands;
    }

    public static string StripComment(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && IsSyntaxEscape(text[i + 1]))
            {
                i++;
                continue;
            }
            if (c is '\'' or '"')
            {
                if (quote == '\0')
                    quote = c;
                else if (quote == c)
                    quote = '\0';
            }
            else if (quote == '\0' && c == '#')
            {
                var end = i + 1;
                while (end < text.Length && char.IsAsciiHexDigit(text[end]))
                    end++;
                var length = end - i - 1;
                var color = length is 3 or 4 or 6 or 8 &&
                    (end == text.Length || char.IsWhiteSpace(text[end]) || text[end] is ';' or ',' or '}');
                if (!color)
                    return text[..i].TrimEnd();
                i = end - 1;
            }
        }
        return text.TrimEnd();
    }

    public static string Unquote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (text.Length < 2 || text[0] is not ('\'' or '"') || text[^1] != text[0] || !HasBalancedQuotes(text))
            return text;
        var tokens = Tokenize(text);
        return tokens.Count == 1 ? tokens[0] : text;
    }

    public static (WindowCriteria? Criteria, string Command) ExtractCriteria(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        text = text.Trim();
        if (!text.StartsWith('['))
            return (null, text);
        var quote = '\0';
        var depth = 0;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && IsSyntaxEscape(text[i + 1]))
            {
                i++;
                continue;
            }
            if (c is '\'' or '"')
            {
                if (quote == '\0')
                    quote = c;
                else if (quote == c)
                    quote = '\0';
                continue;
            }
            if (quote != '\0')
                continue;
            if (c == '[')
                depth++;
            else if (c == ']' && --depth == 0)
                return (WindowCriteria.Parse(text[..(i + 1)]), text[(i + 1)..].Trim());
        }
        return (WindowCriteria.Parse(text), "");
    }

    internal static bool HasBalancedQuotes(string text)
    {
        var quote = '\0';
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\' && i + 1 < text.Length && IsSyntaxEscape(text[i + 1]))
            {
                i++;
                continue;
            }
            if (c is '\'' or '"')
            {
                if (quote == '\0')
                    quote = c;
                else if (quote == c)
                    quote = '\0';
            }
        }
        return quote == '\0';
    }

    internal static bool IsSyntaxEscape(char c) => char.IsWhiteSpace(c) || c is '\\' or '\'' or '"' or ';' or ',' or '#' or '{' or '}' or '[' or ']';

    private static void AddCommand(List<string> commands, string command)
    {
        command = command.Trim();
        if (command.Length > 0)
            commands.Add(command);
    }
}

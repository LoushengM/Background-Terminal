using System.Text;

namespace Background_Terminal.Core;

public enum BuiltInCommandKind
{
    None,
    Clear,
    SetNewline,
    Invalid
}

public sealed record BuiltInCommandResult(
    BuiltInCommandKind Kind,
    string? Newline = null,
    string? Error = null);

public static class BuiltInCommandParser
{
    public static BuiltInCommandResult Parse(string? command)
    {
        string[] parts = (command ?? string.Empty)
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return new BuiltInCommandResult(BuiltInCommandKind.None);
        }

        if (IsClearCommand(parts[0]))
        {
            return parts.Length == 1
                ? new BuiltInCommandResult(BuiltInCommandKind.Clear)
                : new BuiltInCommandResult(BuiltInCommandKind.None);
        }

        if (!string.Equals(parts[0], "bgt", StringComparison.OrdinalIgnoreCase))
        {
            return new BuiltInCommandResult(BuiltInCommandKind.None);
        }

        if (parts.Length != 3 ||
            !string.Equals(parts[1], "newline", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("Usage: bgt newline <escaped-newline>");
        }

        if (!TryParseEscapedNewline(parts[2], out string newline))
        {
            return Invalid("The newline value contains an invalid escape sequence.");
        }

        return new BuiltInCommandResult(BuiltInCommandKind.SetNewline, newline);
    }

    private static BuiltInCommandResult Invalid(string error) =>
        new(BuiltInCommandKind.Invalid, Error: error);

    private static bool TryParseEscapedNewline(string value, out string newline)
    {
        StringBuilder builder = new(value.Length);

        for (int index = 0; index < value.Length; index++)
        {
            char current = value[index];
            if (current != '\\')
            {
                builder.Append(current);
                continue;
            }

            if (index == value.Length - 1)
            {
                newline = string.Empty;
                return false;
            }

            index++;
            current = value[index];
            switch (current)
            {
                case '\\':
                    builder.Append('\\');
                    break;
                case 'r':
                    builder.Append('\r');
                    break;
                case 'n':
                    builder.Append('\n');
                    break;
                case 't':
                    builder.Append('\t');
                    break;
                default:
                    newline = string.Empty;
                    return false;
            }
        }

        newline = builder.ToString();
        return true;
    }

    private static bool IsClearCommand(string command) =>
        string.Equals(command, "clear", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "cls", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(command, "Clear-Host", StringComparison.OrdinalIgnoreCase);
}

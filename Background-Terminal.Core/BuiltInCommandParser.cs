using System.Text.RegularExpressions;

namespace Background_Terminal.Core;

public enum BuiltInCommandKind
{
    None,
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

        if (parts.Length == 0 ||
            !string.Equals(parts[0], "bgt", StringComparison.OrdinalIgnoreCase))
        {
            return new BuiltInCommandResult(BuiltInCommandKind.None);
        }

        if (parts.Length != 3 ||
            !string.Equals(parts[1], "newline", StringComparison.OrdinalIgnoreCase))
        {
            return Invalid("Usage: bgt newline <escaped-newline>");
        }

        if (parts[2].EndsWith('\\'))
        {
            return Invalid("The newline value contains an invalid escape sequence.");
        }

        try
        {
            return new BuiltInCommandResult(
                BuiltInCommandKind.SetNewline,
                Regex.Unescape(parts[2]));
        }
        catch (ArgumentException)
        {
            return Invalid("The newline value contains an invalid escape sequence.");
        }
    }

    private static BuiltInCommandResult Invalid(string error) =>
        new(BuiltInCommandKind.Invalid, Error: error);
}

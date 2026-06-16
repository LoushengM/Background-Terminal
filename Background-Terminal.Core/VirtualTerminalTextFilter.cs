using System.Text;

namespace Background_Terminal.Core;

public sealed class VirtualTerminalTextFilter
{
    private FilterState _state;

    public string Filter(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return string.Empty;
        }

        StringBuilder output = new(text.Length);
        foreach (char character in text)
        {
            switch (_state)
            {
                case FilterState.Text:
                    if (character == '\u001b')
                    {
                        _state = FilterState.Escape;
                    }
                    else if (character != '\0' &&
                        (character >= ' ' || character is '\r' or '\n' or '\t' or '\b'))
                    {
                        output.Append(character);
                    }
                    break;

                case FilterState.Escape:
                    _state = character switch
                    {
                        '[' => FilterState.ControlSequence,
                        ']' => FilterState.OperatingSystemCommand,
                        'P' => FilterState.StringEscape,
                        '_' => FilterState.StringEscape,
                        '^' => FilterState.StringEscape,
                        'X' => FilterState.StringEscape,
                        _ => FilterState.Text
                    };
                    break;

                case FilterState.ControlSequence:
                    if (character is >= '\u0040' and <= '\u007e')
                    {
                        _state = FilterState.Text;
                    }
                    break;

                case FilterState.OperatingSystemCommand:
                    if (character == '\a')
                    {
                        _state = FilterState.Text;
                    }
                    else if (character == '\u001b')
                    {
                        _state = FilterState.OperatingSystemCommandEscape;
                    }
                    break;

                case FilterState.OperatingSystemCommandEscape:
                    _state = character == '\\'
                        ? FilterState.Text
                        : FilterState.OperatingSystemCommand;
                    break;

                case FilterState.StringEscape:
                    if (character == '\u001b')
                    {
                        _state = FilterState.StringEscapeEscape;
                    }
                    break;

                case FilterState.StringEscapeEscape:
                    _state = character == '\\'
                        ? FilterState.Text
                        : FilterState.StringEscape;
                    break;
            }
        }

        return output.ToString();
    }

    public void Reset()
    {
        _state = FilterState.Text;
    }

    private enum FilterState
    {
        Text,
        Escape,
        ControlSequence,
        OperatingSystemCommand,
        OperatingSystemCommandEscape,
        StringEscape,
        StringEscapeEscape
    }
}

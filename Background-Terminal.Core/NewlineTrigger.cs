namespace Background_Terminal.Core;

public sealed class NewlineTrigger
{
    public NewlineTrigger()
    {
    }

    public NewlineTrigger(string triggerCommand, string exitCommand, string newlineString)
    {
        TriggerCommand = triggerCommand;
        ExitCommand = exitCommand;
        NewlineString = newlineString;
    }

    public string TriggerCommand { get; set; } = string.Empty;
    public string ExitCommand { get; set; } = string.Empty;
    public string NewlineString { get; set; } = Environment.NewLine;
}

namespace Background_Terminal.Core;

public sealed class BackgroundTerminalSettings
{
    public string ProcessPath { get; set; } = "cmd.exe";
    public int Key1 { get; set; } = 162;
    public int Key2 { get; set; } = 66;
    public double FontSize { get; set; } = 12;
    public string FontColor { get; set; } = "#FFFFFFFF";
    public string FontFamily { get; set; } = "Consolas";
    public string BackgroundColor { get; set; } = "#D91E1E1E";
    public double WindowOpacity { get; set; } = 1.0;
    public double PosX { get; set; }
    public double PosY { get; set; }
    public double Width { get; set; } = 500;
    public double Height { get; set; } = 500;
    public string RegexFilter { get; set; } = string.Empty;
    public int MaxOutputCharacters { get; set; } = 200_000;
    public List<NewlineTrigger> NewlineTriggers { get; set; } = [];
}

using System.Text.Json;
using System.Text.RegularExpressions;

namespace Background_Terminal.Core;

public sealed record SettingsLoadResult(
    BackgroundTerminalSettings Settings,
    string? RecoveryMessage = null);

public sealed class SettingsService
{
    private static readonly Regex HexColorRegex = new(
        "^#(?:[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$",
        RegexOptions.CultureInvariant);

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };

    public SettingsService(string configPath)
    {
        ConfigPath = configPath;
    }

    public string ConfigPath { get; }

    public SettingsLoadResult Load()
    {
        if (!File.Exists(ConfigPath))
        {
            BackgroundTerminalSettings defaults = new();
            try
            {
                Save(defaults);
                return new SettingsLoadResult(defaults);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                return new SettingsLoadResult(
                    defaults,
                    $"Default settings are in use, but they could not be saved: {exception.Message}");
            }
        }

        try
        {
            string json = File.ReadAllText(ConfigPath);
            BackgroundTerminalSettings settings =
                JsonSerializer.Deserialize<BackgroundTerminalSettings>(json)
                ?? throw new JsonException("The settings document was empty.");

            Normalize(settings);
            try
            {
                Save(settings);
            }
            catch (Exception exception) when (
                exception is IOException or UnauthorizedAccessException)
            {
                // Best-effort save-back; normalized settings remain correct in memory.
            }
            return new SettingsLoadResult(settings);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or JsonException)
        {
            string? backupPath = TryBackUpInvalidConfig();
            BackgroundTerminalSettings defaults = new();

            try
            {
                Save(defaults);
            }
            catch (Exception saveException) when (
                saveException is IOException or UnauthorizedAccessException)
            {
                return new SettingsLoadResult(
                    defaults,
                    $"Settings could not be read or replaced: {saveException.Message}");
            }

            string backupMessage = backupPath is null
                ? string.Empty
                : $" The invalid file was saved as {Path.GetFileName(backupPath)}.";

            return new SettingsLoadResult(
                defaults,
                $"Settings were reset to defaults because the configuration was invalid.{backupMessage}");
        }
    }

    public void Save(BackgroundTerminalSettings settings)
    {
        Normalize(settings);

        string? directory = Path.GetDirectoryName(ConfigPath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string tempPath = ConfigPath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(settings, JsonOptions));
            File.Move(tempPath, ConfigPath, true);
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                try
                {
                    File.Delete(tempPath);
                }
                catch (Exception exception) when (
                    exception is IOException or UnauthorizedAccessException)
                {
                    // Preserve the original save result when temporary cleanup fails.
                }
            }
        }
    }

    public static void Normalize(BackgroundTerminalSettings settings)
    {
        settings.ProcessPath = string.IsNullOrWhiteSpace(settings.ProcessPath)
            ? "cmd.exe"
            : settings.ProcessPath.Trim();
        settings.Key1 = settings.Key1 <= 0 ? 162 : settings.Key1;
        settings.Key2 = settings.Key2 <= 0 ? 66 : settings.Key2;
        settings.FontSize = IsInRange(settings.FontSize, 6, 96) ? settings.FontSize : 12;
        settings.FontColor = IsValidHexColor(settings.FontColor)
            ? settings.FontColor
            : "#FFFFFFFF";
        settings.FontFamily = string.IsNullOrWhiteSpace(settings.FontFamily)
            ? "Consolas"
            : settings.FontFamily.Trim();
        settings.BackgroundColor = IsValidHexColor(settings.BackgroundColor)
            ? settings.BackgroundColor
            : "#D91E1E1E";
        settings.WindowOpacity = settings.WindowOpacity is >= 0.0 and <= 1.0
            && double.IsFinite(settings.WindowOpacity)
            ? settings.WindowOpacity
            : 1.0;
        settings.PosX = double.IsFinite(settings.PosX) ? settings.PosX : 0;
        settings.PosY = double.IsFinite(settings.PosY) ? settings.PosY : 0;
        settings.Width = IsInRange(settings.Width, 100, 10_000) ? settings.Width : 500;
        settings.Height = IsInRange(settings.Height, 100, 10_000) ? settings.Height : 500;
        settings.MaxOutputCharacters = settings.MaxOutputCharacters is >= 10_000 and <= 5_000_000
            ? settings.MaxOutputCharacters
            : 200_000;
        settings.RegexFilter ??= string.Empty;
        settings.NewlineTriggers ??= [];

        try
        {
            _ = new Regex(settings.RegexFilter);
        }
        catch (ArgumentException)
        {
            settings.RegexFilter = string.Empty;
        }

        foreach (NewlineTrigger trigger in settings.NewlineTriggers)
        {
            trigger.TriggerCommand ??= string.Empty;
            trigger.ExitCommand ??= string.Empty;
            trigger.NewlineString ??= Environment.NewLine;
        }
    }

    public static bool IsValidHexColor(string? value) =>
        value is not null && HexColorRegex.IsMatch(value);

    public static bool IsInRange(double value, double minimum, double maximum) =>
        double.IsFinite(value) && value >= minimum && value <= maximum;

    private string? TryBackUpInvalidConfig()
    {
        try
        {
            string backupPath =
                $"{ConfigPath}.corrupt-{DateTime.UtcNow:yyyyMMddHHmmssfff}";
            File.Move(ConfigPath, backupPath, true);
            return backupPath;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }
}

using System.Text.Json;
using Background_Terminal.Core;

namespace Background_Terminal.Core.Tests;

[TestClass]
public sealed class SettingsServiceTests
{
    [TestMethod]
    public void Load_MissingConfig_CreatesDefaults()
    {
        using TempDirectory temp = new();
        string configPath = Path.Combine(temp.Path, "nested", "settings.json");
        SettingsService service = new(configPath);

        SettingsLoadResult result = service.Load();

        Assert.IsNull(result.RecoveryMessage);
        AssertDefaultSettings(result.Settings);
        Assert.IsTrue(File.Exists(configPath));
        Assert.IsNotNull(
            JsonSerializer.Deserialize<BackgroundTerminalSettings>(
                File.ReadAllText(configPath)));
    }

    [TestMethod]
    public void Load_MalformedConfig_BacksItUpAndRestoresDefaults()
    {
        using TempDirectory temp = new();
        string configPath = Path.Combine(temp.Path, "settings.json");
        const string malformedJson = "{ definitely not json";
        File.WriteAllText(configPath, malformedJson);
        SettingsService service = new(configPath);

        SettingsLoadResult result = service.Load();

        AssertDefaultSettings(result.Settings);
        StringAssert.Contains(result.RecoveryMessage, "reset to defaults");

        string[] backups =
            Directory.GetFiles(temp.Path, "settings.json.corrupt-*");
        Assert.HasCount(1, backups);
        Assert.AreEqual(malformedJson, File.ReadAllText(backups[0]));

        BackgroundTerminalSettings? saved =
            JsonSerializer.Deserialize<BackgroundTerminalSettings>(
                File.ReadAllText(configPath));
        Assert.IsNotNull(saved);
        AssertDefaultSettings(saved);
    }

    [TestMethod]
    public void Load_UsesSerializerOptionsForCaseInsensitiveProperties()
    {
        using TempDirectory temp = new();
        string configPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(
            configPath,
            """{"processpath":"pwsh.exe","fontsize":15}""");
        SettingsService service = new(configPath);

        SettingsLoadResult result = service.Load();

        Assert.IsNull(result.RecoveryMessage);
        Assert.AreEqual("pwsh.exe", result.Settings.ProcessPath);
        Assert.AreEqual(15, result.Settings.FontSize);
    }

    [TestMethod]
    public void Load_RepeatedInvalidConfigs_KeepDistinctBackups()
    {
        using TempDirectory temp = new();
        string configPath = Path.Combine(temp.Path, "settings.json");
        const string malformedJson = "{ definitely not json";
        SettingsService service = new(configPath);

        File.WriteAllText(configPath, malformedJson);
        service.Load();

        File.WriteAllText(configPath, malformedJson);
        service.Load();

        string[] backups =
            Directory.GetFiles(temp.Path, "settings.json.corrupt-*");
        Assert.AreEqual(2, backups.Length);
        Assert.AreEqual(
            malformedJson,
            File.ReadAllText(backups[0]));
        Assert.AreEqual(
            malformedJson,
            File.ReadAllText(backups[1]));
        Assert.AreNotEqual(
            Path.GetFileName(backups[0]),
            Path.GetFileName(backups[1]));
    }

    [TestMethod]
    public void Load_ReadFailure_ReturnsDefaultsWithoutReplacingConfig()
    {
        using TempDirectory temp = new();
        string configPath = Path.Combine(temp.Path, "settings.json");
        const string originalJson = """{"ProcessPath":"locked.exe"}""";
        File.WriteAllText(configPath, originalJson);
        SettingsService service = new(configPath);

        using (FileStream lockStream = new(
            configPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            SettingsLoadResult result = service.Load();

            AssertDefaultSettings(result.Settings);
            StringAssert.Contains(result.RecoveryMessage, "left untouched");
        }

        Assert.AreEqual(originalJson, File.ReadAllText(configPath));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "settings.json.corrupt-*").Length);
    }

    [TestMethod]
    public void Load_PartialConfig_PreservesPropertyDefaultsAndNormalizesValues()
    {
        using TempDirectory temp = new();
        string configPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(
            configPath,
            """
            {
              "FontSize": 18,
              "FontFamily": "  Cascadia Mono  "
            }
            """);
        SettingsService service = new(configPath);

        SettingsLoadResult result = service.Load();

        Assert.IsNull(result.RecoveryMessage);
        Assert.AreEqual(18, result.Settings.FontSize);
        Assert.AreEqual("Cascadia Mono", result.Settings.FontFamily);
        Assert.AreEqual("cmd.exe", result.Settings.ProcessPath);
        Assert.AreEqual(string.Empty, result.Settings.WorkingDirectory);
        Assert.AreEqual(500, result.Settings.Width);
        Assert.AreEqual(200_000, result.Settings.MaxOutputCharacters);
        Assert.IsNotNull(result.Settings.NewlineTriggers);
    }

    [TestMethod]
    public void Save_ReplacesConfigAndRemovesStagingFile()
    {
        using TempDirectory temp = new();
        string configPath = Path.Combine(temp.Path, "settings.json");
        File.WriteAllText(configPath, """{"ProcessPath":"old.exe"}""");
        SettingsService service = new(configPath);
        BackgroundTerminalSettings settings = new()
        {
            ProcessPath = "pwsh.exe",
            FontSize = 14
        };

        service.Save(settings);

        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "settings.json.*.staging").Length);
        BackgroundTerminalSettings? saved =
            JsonSerializer.Deserialize<BackgroundTerminalSettings>(
                File.ReadAllText(configPath));
        Assert.IsNotNull(saved);
        Assert.AreEqual("pwsh.exe", saved.ProcessPath);
        Assert.AreEqual(14, saved.FontSize);
    }

    [TestMethod]
    public void Save_WhenDestinationIsLocked_LeavesExistingConfigUntouched()
    {
        using TempDirectory temp = new();
        string configPath = Path.Combine(temp.Path, "settings.json");
        const string originalJson = """{"ProcessPath":"old.exe"}""";
        File.WriteAllText(configPath, originalJson);
        SettingsService service = new(configPath);

        Exception? saveException = null;
        using (FileStream lockStream = new(
            configPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            try
            {
                service.Save(new BackgroundTerminalSettings
                {
                    ProcessPath = "new.exe"
                });
            }
            catch (Exception exception)
                when (exception is IOException or UnauthorizedAccessException)
            {
                saveException = exception;
            }
        }

        Assert.IsNotNull(saveException);
        Assert.AreEqual(originalJson, File.ReadAllText(configPath));
        Assert.AreEqual(0, Directory.GetFiles(temp.Path, "settings.json.*.staging").Length);
    }

    [TestMethod]
    public void Normalize_RepairsInvalidSettings()
    {
        BackgroundTerminalSettings settings = new()
        {
            ProcessPath = " ",
            WorkingDirectory = "\t",
            Key1 = 0,
            Key2 = -1,
            FontSize = double.NaN,
            FontColor = "blue",
            FontFamily = "\t",
            BackgroundColor = "not-a-color",
            WindowOpacity = 2.5,
            PosX = double.PositiveInfinity,
            PosY = double.NegativeInfinity,
            Width = 99,
            Height = 10_001,
            RegexFilter = "[",
            MaxOutputCharacters = 9_999,
            NewlineTriggers =
            [
                new NewlineTrigger
                {
                    TriggerCommand = null!,
                    ExitCommand = null!,
                    NewlineString = null!
                }
            ]
        };

        SettingsService.Normalize(settings);

        AssertDefaultSettings(settings);
        Assert.AreEqual(string.Empty, settings.RegexFilter);
        Assert.HasCount(1, settings.NewlineTriggers);
        Assert.AreEqual(string.Empty, settings.NewlineTriggers[0].TriggerCommand);
        Assert.AreEqual(string.Empty, settings.NewlineTriggers[0].ExitCommand);
        Assert.AreEqual(
            Environment.NewLine,
            settings.NewlineTriggers[0].NewlineString);
    }

    [TestMethod]
    public void Normalize_LeavesNullNewlineTriggerEntriesOut()
    {
        BackgroundTerminalSettings settings = new()
        {
            NewlineTriggers =
            [
                null!,
                new NewlineTrigger
                {
                    TriggerCommand = "  run  ",
                    ExitCommand = null!,
                    NewlineString = null!
                }
            ]
        };

        SettingsService.Normalize(settings);

        Assert.HasCount(1, settings.NewlineTriggers);
        Assert.AreEqual("  run  ", settings.NewlineTriggers[0].TriggerCommand);
        Assert.AreEqual(string.Empty, settings.NewlineTriggers[0].ExitCommand);
        Assert.AreEqual(Environment.NewLine, settings.NewlineTriggers[0].NewlineString);
    }

    private static void AssertDefaultSettings(
        BackgroundTerminalSettings settings)
    {
        Assert.AreEqual("cmd.exe", settings.ProcessPath);
        Assert.AreEqual(string.Empty, settings.WorkingDirectory);
        Assert.AreEqual(162, settings.Key1);
        Assert.AreEqual(66, settings.Key2);
        Assert.AreEqual(12, settings.FontSize);
        Assert.AreEqual("#FFFFFFFF", settings.FontColor);
        Assert.AreEqual("Consolas", settings.FontFamily);
        Assert.AreEqual("#D91E1E1E", settings.BackgroundColor);
        Assert.AreEqual(1.0, settings.WindowOpacity);
        Assert.AreEqual(0, settings.PosX);
        Assert.AreEqual(0, settings.PosY);
        Assert.AreEqual(500, settings.Width);
        Assert.AreEqual(500, settings.Height);
        Assert.AreEqual(200_000, settings.MaxOutputCharacters);
    }
}

using Background_Terminal.Core;
using CoreMeter;
using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Background_Terminal;

public partial class MainWindow : Window
{
    private static readonly BrushConverter BrushConverter = new();
    private static readonly Regex PowerShellPromptRegex = new(
        @"(?:^|[\r\n])(PS [^\r\n>]+>\s*)",
        RegexOptions.CultureInvariant);
    private static readonly string ConfigPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BackgroundTerminal",
        "config.json");

    private readonly SettingsService _settingsService;
    private TerminalWindow _terminalWindow;
    private CoreMeterUtility? _coreMeterUtility;
    private readonly TerminalOutputBuffer _outputBuffer;
    private readonly VirtualTerminalTextFilter _terminalTextFilter = new();
    private readonly DispatcherTimer _outputTimer;
    private readonly SemaphoreSlim _sessionLifecycle = new(1, 1);

    private BackgroundTerminalSettings _settings;
    private ITerminalSession? _terminalSession;
    private Regex? _regex;
    private string? _currentTrigger;
    private string _newlineString = "\r";
    private bool _terminalWindowActive;
    private bool _terminalWindowLocked = true;
    private bool _awaitingKey1;
    private bool _awaitingKey2;
    private bool _isClosing;
    private bool _shutdownComplete;
    private bool _fallbackNoticeShown;
    private Key? _key1;
    private Key? _key2;

    public MainWindow()
    {
        InitializeComponent();

        _settingsService = new SettingsService(ConfigPath);
        SettingsLoadResult loadResult = LoadSettings();
        _settings = loadResult.Settings;
        _outputBuffer = new TerminalOutputBuffer(_settings.MaxOutputCharacters);

        NewlineTriggers = new ObservableCollection<NewlineTrigger>(
            _settings.NewlineTriggers);
        DataContext = this;

        _terminalWindow = CreateTerminalWindow();
        ApplySettingsToTerminalWindow();
        ShowTerminalWindow(_terminalWindow);
        _coreMeterUtility = CreateCoreMeterUtility(_terminalWindow);
        _coreMeterUtility.Lock();
        _terminalWindow.SetWindowLocked(true);

        _outputTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(50),
            DispatcherPriority.Background,
            FlushTerminalOutput,
            Dispatcher);
        _outputTimer.Start();

        PopulateSettingsControls();

        Win32Interop.KeyTriggered = KeyTriggered;
        try
        {
            Win32Interop.SetKeyhook();
        }
        catch (Win32Exception exception)
        {
            QueueOutput(
                $"The global activation shortcut is unavailable: {exception.Message}" +
                Environment.NewLine);
        }

        if (!string.IsNullOrWhiteSpace(loadResult.RecoveryMessage))
        {
            QueueOutput(loadResult.RecoveryMessage + Environment.NewLine);
        }
    }

    public ObservableCollection<NewlineTrigger> NewlineTriggers { get; }

    private TerminalWindow CreateTerminalWindow()
    {
        TerminalWindow terminalWindow = new(
            SendCommandAsync,
            SendInterruptAsync,
            TerminalWindowUiUpdate);
        terminalWindow.InputActivated += TerminalWindow_InputActivated;
        terminalWindow.InputDeactivated += TerminalWindow_InputDeactivated;
        return terminalWindow;
    }

    private static void ShowTerminalWindow(TerminalWindow terminalWindow)
    {
        terminalWindow.Show();
        IntPtr terminalHandle = new WindowInteropHelper(terminalWindow).Handle;
        Win32Interop.HideWindowFromAltTabMenu(terminalHandle);
    }

    private static CoreMeterUtility CreateCoreMeterUtility(TerminalWindow terminalWindow)
    {
        IntPtr terminalHandle = new WindowInteropHelper(terminalWindow).Handle;
        return new CoreMeterUtility(terminalHandle);
    }

    private void TerminalWindow_InputActivated(object? sender, EventArgs e)
    {
        _terminalWindowActive = true;
    }

    private void TerminalWindow_InputDeactivated(object? sender, EventArgs e)
    {
        _terminalWindowActive = false;
    }

    private SettingsLoadResult LoadSettings()
    {
        try
        {
            return _settingsService.Load();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            return new SettingsLoadResult(
                new BackgroundTerminalSettings(),
                $"Settings could not be loaded; defaults are in use: {exception.Message}");
        }
    }

    private void PopulateSettingsControls()
    {
        _key1 = KeyInterop.KeyFromVirtualKey(_settings.Key1);
        _key2 = KeyInterop.KeyFromVirtualKey(_settings.Key2);
        try
        {
            _regex = CreateRegex(_settings.RegexFilter);
        }
        catch (ArgumentException)
        {
            _regex = null;
            QueueOutput(
                "The saved regex filter was invalid and has been disabled for this session." +
                Environment.NewLine);
        }

        Process_TextBox.Text = _settings.ProcessPath;
        WorkingDirectory_TextBox.Text = _settings.WorkingDirectory;
        Key1_Button.Content = _key1?.ToString() ?? string.Empty;
        Key2_Button.Content = _key2?.ToString() ?? string.Empty;
        FontSize_TextBox.Text = _settings.FontSize.ToString(CultureInfo.CurrentCulture);
        FontColor_TextBox.Text = _settings.FontColor;
        FontFamily_TextBox.Text = _settings.FontFamily;
        BackgroundColor_TextBox.Text = _settings.BackgroundColor;
        Opacity_TextBox.Text = _settings.WindowOpacity.ToString(CultureInfo.CurrentCulture);
        PosX_TextBox.Text = _settings.PosX.ToString(CultureInfo.CurrentCulture);
        PosY_TextBox.Text = _settings.PosY.ToString(CultureInfo.CurrentCulture);
        Width_TextBox.Text = _settings.Width.ToString(CultureInfo.CurrentCulture);
        Height_TextBox.Text = _settings.Height.ToString(CultureInfo.CurrentCulture);
        RegexFilter_TextBox.Text = _settings.RegexFilter;
    }

    private void ApplySettingsToTerminalWindow()
    {
        Brush foreground = (Brush?)BrushConverter.ConvertFromString(_settings.FontColor)
            ?? Brushes.White;
        FontFamily fontFamily = new(_settings.FontFamily);

        _terminalWindow.TerminalData_TextBox.FontSize = _settings.FontSize;
        _terminalWindow.Input_TextBox.FontSize = _settings.FontSize;
        _terminalWindow.TerminalData_TextBox.FontFamily = fontFamily;
        _terminalWindow.Input_TextBox.FontFamily = fontFamily;
        _terminalWindow.TerminalData_TextBox.Foreground = foreground;
        _terminalWindow.Input_TextBox.Foreground = foreground;
        _terminalWindow.SetCursorColor(foreground);
        Brush background = (Brush?)BrushConverter.ConvertFromString(_settings.BackgroundColor)
            ?? new SolidColorBrush(Color.FromArgb(0xD9, 0x1E, 0x1E, 0x1E));
        _terminalWindow.ApplyAppearance(background, _settings.WindowOpacity);
        ApplyTerminalWindowBounds();
    }

    private void ApplyTerminalWindowBounds()
    {
        CoreMeterUtility? coreMeterUtility = _coreMeterUtility;
        if (_terminalWindowLocked && coreMeterUtility is not null)
        {
            coreMeterUtility.Unlock();

            try
            {
                SetTerminalWindowBounds();
            }
            finally
            {
                coreMeterUtility.Lock();
            }
        }
        else
        {
            SetTerminalWindowBounds();
        }

        TerminalWindowUiUpdate();
    }

    private void SetTerminalWindowBounds()
    {
        if (_terminalWindow.WindowState != WindowState.Normal)
        {
            _terminalWindow.WindowState = WindowState.Normal;
        }

        _terminalWindow.Left = _settings.PosX;
        _terminalWindow.Top = _settings.PosY;
        _terminalWindow.Width = _settings.Width;
        _terminalWindow.Height = _settings.Height;
    }

    private void TerminalWindowUiUpdate()
    {
        PosX_TextBox.Text = _terminalWindow.Left.ToString(CultureInfo.CurrentCulture);
        PosY_TextBox.Text = _terminalWindow.Top.ToString(CultureInfo.CurrentCulture);
        Width_TextBox.Text = _terminalWindow.Width.ToString(CultureInfo.CurrentCulture);
        Height_TextBox.Text = _terminalWindow.Height.ToString(CultureInfo.CurrentCulture);
    }

    private void LockTerminalWindow()
    {
        FlushTerminalOutput(null, EventArgs.Empty);
        _terminalWindow.DeactivateInput();
        _terminalWindow.SetWindowLocked(true);
        _coreMeterUtility?.Lock();
        _terminalWindow.RefreshCursorAfterWindowLock();
        TerminalWindowUiUpdate();
    }

    private async Task RestartTerminalSessionAsync()
    {
        await _sessionLifecycle.WaitAsync();
        try
        {
            await DisposeTerminalSessionAsync();
            _terminalTextFilter.Reset();

            if (_isClosing)
            {
                return;
            }

            ITerminalSession session = await TerminalSessionFactory.CreateAsync();
            session.OutputReceived += TerminalSession_OutputReceived;
            session.Exited += TerminalSession_Exited;
            _terminalSession = session;
            _newlineString = session.InputNewLine;

            if (session is IWorkingDirectoryTerminalSession workingDirectorySession)
            {
                workingDirectorySession.WorkingDirectory = _settings.WorkingDirectory;
            }

            if (session is RedirectedProcessTerminalSession &&
                !_fallbackNoticeShown)
            {
                _fallbackNoticeShown = true;
                QueueOutput(
                    "[ConPTY is unavailable in this session; using redirected " +
                    $"process mode.]{Environment.NewLine}");
            }

            try
            {
                await session.StartAsync(_settings.ProcessPath);
            }
            catch (Exception exception)
            {
                if (ReferenceEquals(_terminalSession, session))
                {
                    _terminalSession = null;
                }

                session.OutputReceived -= TerminalSession_OutputReceived;
                session.Exited -= TerminalSession_Exited;
                try
                {
                    await session.DisposeAsync();
                }
                catch (Exception disposeException)
                {
                    QueueOutput(
                        $"Unable to release the failed terminal session: " +
                        $"{disposeException.Message}{Environment.NewLine}");
                }
                QueueOutput(
                    $"Unable to start '{_settings.ProcessPath}': {exception.Message}" +
                    Environment.NewLine);

                RestoreMainWindow();
                MessageBox.Show(
                    this,
                    "There was an error starting the terminal process. Check the Process " +
                    $"input and apply changes to retry.{Environment.NewLine}{Environment.NewLine}" +
                    exception.Message,
                    "Background Terminal",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }
        finally
        {
            _sessionLifecycle.Release();
        }
    }

    private async Task DisposeTerminalSessionAsync()
    {
        ITerminalSession? session = _terminalSession;
        _terminalSession = null;
        if (session is null)
        {
            return;
        }

        session.OutputReceived -= TerminalSession_OutputReceived;
        session.Exited -= TerminalSession_Exited;

        try
        {
            await session.StopAsync();
        }
        catch (Exception exception)
        {
            QueueOutput($"Unable to stop the terminal cleanly: {exception.Message}{Environment.NewLine}");
        }

        try
        {
            await session.DisposeAsync();
        }
        catch (Exception exception)
        {
            QueueOutput(
                $"Unable to release the terminal session: {exception.Message}" +
                Environment.NewLine);
        }
    }

    private void TerminalSession_OutputReceived(string output)
    {
        QueueOutput(output);
    }

    private void TerminalSession_Exited(int exitCode)
    {
        if (!_isClosing)
        {
            QueueOutput(
                $"{Environment.NewLine}[Terminal exited with code {exitCode}]" +
                Environment.NewLine);
        }
    }

    private void QueueOutput(string? text)
    {
        _outputBuffer.Append(text);
    }

    private void FlushTerminalOutput(object? sender, EventArgs e)
    {
        string output = _outputBuffer.Drain();
        if (output.Length == 0)
        {
            return;
        }

        output = _terminalTextFilter.Filter(output);

        if (_regex is not null)
        {
            output = _regex.Replace(output, string.Empty);
        }

        if (output.Length == 0)
        {
            return;
        }

        _terminalWindow.TerminalData_TextBox.AppendText(output);
        TrimRenderedOutput();
        _terminalWindow.TerminalData_TextBox.ScrollToEnd();
    }

    private void TrimRenderedOutput()
    {
        string renderedText = _terminalWindow.TerminalData_TextBox.Text;
        if (renderedText.Length <= _settings.MaxOutputCharacters)
        {
            return;
        }

        int tailLength = Math.Max(
            0,
            _settings.MaxOutputCharacters - Environment.NewLine.Length);
        string tail = renderedText[^tailLength..];
        _terminalWindow.TerminalData_TextBox.Text = tail;
        _terminalWindow.TerminalData_TextBox.CaretIndex = tail.Length;
    }

    private void ClearRenderedOutput()
    {
        string? promptLine = FindLastPowerShellPromptLine(
            _terminalWindow.TerminalData_TextBox.Text);

        _outputBuffer.Clear();
        _terminalWindow.TerminalData_TextBox.Clear();

        if (promptLine is not null)
        {
            _terminalWindow.TerminalData_TextBox.Text = promptLine;
            _terminalWindow.TerminalData_TextBox.CaretIndex = promptLine.Length;
            _terminalWindow.TerminalData_TextBox.ScrollToEnd();
        }
    }

    private static string? FindLastPowerShellPromptLine(string text)
    {
        MatchCollection matches = PowerShellPromptRegex.Matches(text);
        if (matches.Count == 0)
        {
            return null;
        }

        return matches[^1].Groups[1].Value.TrimEnd();
    }

    private async Task SendCommandAsync(string command)
    {
        BuiltInCommandResult builtIn = BuiltInCommandParser.Parse(command);
        if (builtIn.Kind != BuiltInCommandKind.None)
        {
            if (builtIn.Kind == BuiltInCommandKind.Clear)
            {
                ClearRenderedOutput();
            }
            else if (builtIn.Kind == BuiltInCommandKind.SetNewline)
            {
                QueueOutput(command + Environment.NewLine);
                _newlineString = builtIn.Newline ?? "\r";
            }
            else
            {
                QueueOutput(command + Environment.NewLine);
                QueueOutput((builtIn.Error ?? "Invalid Background Terminal command.") +
                    Environment.NewLine);
            }

            return;
        }

        ITerminalSession? session = _terminalSession;
        if (session is null || !session.IsRunning)
        {
            QueueOutput("The terminal process is not running." + Environment.NewLine);
            return;
        }

        try
        {
            await session.WriteAsync(command + _newlineString);
            UpdateNewlineTrigger(command);
        }
        catch (Exception exception)
        {
            QueueOutput($"Unable to send input: {exception.Message}{Environment.NewLine}");
        }
    }

    private async Task SendInterruptAsync()
    {
        ITerminalSession? session = _terminalSession;
        if (session is null || !session.IsRunning)
        {
            return;
        }

        try
        {
            await session.SendInterruptAsync();
            if (session is RedirectedProcessTerminalSession)
            {
                await RestartTerminalSessionAsync();
            }
        }
        catch (Exception exception)
        {
            QueueOutput($"Unable to interrupt the terminal: {exception.Message}{Environment.NewLine}");
        }
    }

    private void UpdateNewlineTrigger(string command)
    {
        foreach (NewlineTrigger trigger in _settings.NewlineTriggers)
        {
            if (!string.IsNullOrEmpty(trigger.TriggerCommand) &&
                command.StartsWith(trigger.TriggerCommand, StringComparison.Ordinal))
            {
                if (TryUnescape(trigger.NewlineString, out string newline))
                {
                    _currentTrigger = trigger.TriggerCommand;
                    _newlineString = newline;
                }
                else
                {
                    QueueOutput(
                        $"Newline trigger '{trigger.TriggerCommand}' contains an invalid " +
                        $"escape sequence.{Environment.NewLine}");
                }
            }
            else if (_currentTrigger == trigger.TriggerCommand &&
                !string.IsNullOrEmpty(trigger.ExitCommand) &&
                command.StartsWith(trigger.ExitCommand, StringComparison.Ordinal))
            {
                _currentTrigger = null;
                _newlineString = "\r";
            }
        }
    }

    private static bool TryUnescape(string value, out string unescaped)
    {
        try
        {
            unescaped = Regex.Unescape(value);
            return true;
        }
        catch (ArgumentException)
        {
            unescaped = "\r";
            return false;
        }
    }

    private static Regex? CreateRegex(string? pattern)
    {
        if (string.IsNullOrEmpty(pattern))
        {
            return null;
        }

        return new Regex(pattern, RegexOptions.CultureInvariant);
    }

    private void TrayIcon_LeftMouseDown(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            RestoreMainWindow();
        }
        else
        {
            WindowState = WindowState.Minimized;
        }
    }

    private void TerminalWindowLockedButton_Click(object sender, RoutedEventArgs e)
    {
        _terminalWindowLocked = !_terminalWindowLocked;
        TerminalWindowLocked_Button.Content = _terminalWindowLocked ? "Locked" : "Unlocked";

        if (_terminalWindowLocked)
        {
            LockTerminalWindow();
        }
        else
        {
            _terminalWindow.SetWindowLocked(false);
            _coreMeterUtility?.Unlock();
        }
    }

    private void Key1Button_Click(object sender, RoutedEventArgs e)
    {
        Key1_Button.Content = "Press Key...";

        if (_awaitingKey2)
        {
            Key2_Button.Content = _key2?.ToString() ?? string.Empty;
            _awaitingKey2 = false;
        }

        _awaitingKey1 = true;
    }

    private void Key2Button_Click(object sender, RoutedEventArgs e)
    {
        Key2_Button.Content = "Press Key...";

        if (_awaitingKey1)
        {
            Key1_Button.Content = _key1?.ToString() ?? string.Empty;
            _awaitingKey1 = false;
        }

        _awaitingKey2 = true;
    }

    private void AddNewlineTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        NewlineTriggers.Add(new NewlineTrigger(
            "Trigger Command",
            "Exit Command",
            "Newline Character"));
    }

    private void DeleteNewlineTriggerButton_Click(object sender, RoutedEventArgs e)
    {
        if (NewlineTrigger_ListBox.SelectedItem is NewlineTrigger selectedTrigger)
        {
            NewlineTriggers.Remove(selectedTrigger);
        }
    }

    private void NewlineTriggerTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        NewlineTrigger_ListBox.SelectedItem = ((TextBox)sender).DataContext;
    }

    private async void ApplyChangesButton_Click(object sender, RoutedEventArgs e)
    {
        if (!TryReadSettingsFromControls(out BackgroundTerminalSettings? updatedSettings))
        {
            return;
        }

        bool processChanged = !string.Equals(
            _settings.ProcessPath,
            updatedSettings.ProcessPath,
            StringComparison.OrdinalIgnoreCase);
        bool workingDirectoryChanged = !string.Equals(
            _settings.WorkingDirectory,
            updatedSettings.WorkingDirectory,
            StringComparison.OrdinalIgnoreCase);

        try
        {
            _settingsService.Save(updatedSettings);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(
                this,
                $"Settings could not be saved:{Environment.NewLine}{Environment.NewLine}" +
                exception.Message,
                "Background Terminal",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
            return;
        }

        _settings = updatedSettings;
        _regex = CreateRegex(_settings.RegexFilter);
        ApplySettingsToTerminalWindow();

        if (processChanged || workingDirectoryChanged)
        {
            await RestartTerminalSessionAsync();
        }
    }

    private bool TryReadSettingsFromControls(
        [NotNullWhen(true)] out BackgroundTerminalSettings? updatedSettings)
    {
        updatedSettings = null;

        if (_key1 is null || _key2 is null)
        {
            ShowValidationError("Choose both activation keys.");
            return false;
        }

        string processPath = Process_TextBox.Text.Trim();
        if (processPath.Length == 0)
        {
            ShowValidationError("Process cannot be empty.");
            return false;
        }

        string workingDirectory = WorkingDirectory_TextBox.Text.Trim();
        if (!TryResolveWorkingDirectory(workingDirectory, out string? resolvedWorkingDirectory))
        {
            return false;
        }

        if (!SettingsService.IsValidHexColor(FontColor_TextBox.Text))
        {
            ShowValidationError("Font color must be a 6- or 8-digit hexadecimal color.");
            return false;
        }

        if (!TryReadNumber(
                FontSize_TextBox.Text,
                6,
                96,
                "Font size must be a finite number from 6 through 96.",
                out double fontSize) ||
            !TryReadFiniteNumber(
                PosX_TextBox.Text,
                "X position must be a finite number.",
                out double posX) ||
            !TryReadFiniteNumber(
                PosY_TextBox.Text,
                "Y position must be a finite number.",
                out double posY) ||
            !TryReadNumber(
                Width_TextBox.Text,
                100,
                10_000,
                "Width must be a finite number from 100 through 10000.",
                out double width) ||
            !TryReadNumber(
                Height_TextBox.Text,
                100,
                10_000,
                "Height must be a finite number from 100 through 10000.",
                out double height))
        {
            return false;
        }

        string fontFamilyText = FontFamily_TextBox.Text.Trim();
        if (fontFamilyText.Length == 0)
        {
            ShowValidationError("Font family cannot be empty.");
            return false;
        }

        if (!SettingsService.IsValidHexColor(BackgroundColor_TextBox.Text))
        {
            ShowValidationError("Background color must be a 6- or 8-digit hexadecimal color.");
            return false;
        }

        if (!TryReadNumber(
                Opacity_TextBox.Text,
                0.0,
                1.0,
                "Opacity must be a finite number from 0.0 through 1.0.",
                out double opacity))
        {
            return false;
        }

        FontFamily? fontFamily = Fonts.SystemFontFamilies.FirstOrDefault(
            family => string.Equals(
                family.Source,
                fontFamilyText,
                StringComparison.OrdinalIgnoreCase));
        if (fontFamily is null)
        {
            ShowValidationError("Font family must name an installed system font.");
            return false;
        }

        Regex? regex;
        try
        {
            regex = CreateRegex(RegexFilter_TextBox.Text);
        }
        catch (ArgumentException)
        {
            ShowValidationError("There was an error interpreting the regex filter.");
            return false;
        }

        _ = regex;
        updatedSettings = new BackgroundTerminalSettings
        {
            ProcessPath = processPath,
            WorkingDirectory = resolvedWorkingDirectory ?? string.Empty,
            Key1 = KeyInterop.VirtualKeyFromKey(_key1.Value),
            Key2 = KeyInterop.VirtualKeyFromKey(_key2.Value),
            FontSize = fontSize,
            FontColor = FontColor_TextBox.Text,
            FontFamily = fontFamily.Source,
            BackgroundColor = BackgroundColor_TextBox.Text,
            WindowOpacity = Math.Round(opacity, 4),
            PosX = posX,
            PosY = posY,
            Width = width,
            Height = height,
            RegexFilter = RegexFilter_TextBox.Text,
            MaxOutputCharacters = _settings.MaxOutputCharacters,
            NewlineTriggers = NewlineTriggers
                .Select(trigger => new NewlineTrigger(
                    trigger.TriggerCommand,
                    trigger.ExitCommand,
                    trigger.NewlineString))
                .ToList()
        };
        return true;
    }

    private bool TryResolveWorkingDirectory(
        string workingDirectory,
        out string? resolvedWorkingDirectory)
    {
        resolvedWorkingDirectory = null;

        if (workingDirectory.Length == 0)
        {
            return true;
        }

        string expanded = Environment.ExpandEnvironmentVariables(workingDirectory);
        string fullPath;
        try
        {
            fullPath = Path.GetFullPath(expanded);
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            ShowValidationError("Working directory must be a valid folder path.");
            return false;
        }

        if (!Directory.Exists(fullPath))
        {
            ShowValidationError("Working directory must exist.");
            return false;
        }

        resolvedWorkingDirectory = fullPath;
        return true;
    }

    private bool TryReadNumber(
        string text,
        double minimum,
        double maximum,
        string error,
        out double value)
    {
        if (double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out value) &&
            SettingsService.IsInRange(value, minimum, maximum))
        {
            return true;
        }

        ShowValidationError(error);
        return false;
    }

    private bool TryReadFiniteNumber(string text, string error, out double value)
    {
        if (double.TryParse(
                text,
                NumberStyles.Float,
                CultureInfo.CurrentCulture,
                out value) &&
            double.IsFinite(value))
        {
            return true;
        }

        ShowValidationError(error);
        return false;
    }

    private void ShowValidationError(string message)
    {
        MessageBox.Show(
            this,
            message,
            "Background Terminal",
            MessageBoxButton.OK,
            MessageBoxImage.Warning);
    }

    private void MainWindow_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
        await RestartTerminalSessionAsync();
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void ExitButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void MainWindow_Closing(object? sender, CancelEventArgs e)
    {
        if (_shutdownComplete)
        {
            return;
        }

        e.Cancel = true;
        if (_isClosing)
        {
            return;
        }

        _isClosing = true;
        _ = ShutdownAsync();
    }

    private void MainWindow_StateChanged(object sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Hide();
        }
        else
        {
            Show();
        }
    }

    private void MainWindow_Closed(object sender, EventArgs e)
    {
    }

    private void RestoreMainWindow()
    {
        if (!IsVisible)
        {
            Show();
        }

        if (WindowState == WindowState.Minimized)
        {
            WindowState = WindowState.Normal;
        }

        Activate();
        Focus();
    }

    private async Task ShutdownAsync()
    {
        try
        {
            _outputTimer.Stop();
            if (!Win32Interop.DestroyKeyhook(out Win32Exception? hookException) &&
                hookException is not null)
            {
                QueueOutput(
                    $"Unable to uninstall the global keyboard hook: " +
                    $"{hookException.Message}{Environment.NewLine}");
            }

            await _sessionLifecycle.WaitAsync();
            try
            {
                await DisposeTerminalSessionAsync();
            }
            finally
            {
                _sessionLifecycle.Release();
            }

            await Dispatcher.InvokeAsync(() =>
            {
                if (_shutdownComplete)
                {
                    return;
                }

                _shutdownComplete = true;
                _terminalWindow.Close();
                Close();
            });
        }
        finally
        {
            _sessionLifecycle.Dispose();
        }
    }

    private void KeyTriggered(int keyCode)
    {
        if (!Dispatcher.CheckAccess())
        {
            Dispatcher.BeginInvoke(() => KeyTriggered(keyCode));
            return;
        }

        if (_key1 is not null && _key2 is not null)
        {
            int firstVirtualKey = KeyInterop.VirtualKeyFromKey(_key1.Value);
            int secondVirtualKey = KeyInterop.VirtualKeyFromKey(_key2.Value);

            if (keyCode == secondVirtualKey && Win32Interop.IsKeyDown(firstVirtualKey))
            {
                _terminalWindow.Input_TextBox.Clear();

                if (!_terminalWindowActive)
                {
                    _terminalWindow.ActivateInput();
                    Win32Interop.ClickSimulateFocus(_terminalWindow);
                    IntPtr terminalHandle = new WindowInteropHelper(_terminalWindow).Handle;
                    Win32Interop.SetForegroundWindow(terminalHandle);
                    Keyboard.Focus(_terminalWindow.Input_TextBox);
                    _terminalWindowActive = true;
                }
                else
                {
                    _terminalWindow.DeactivateInput();
                    _terminalWindowActive = false;
                }
            }
        }

        if (_awaitingKey1)
        {
            _key1 = KeyInterop.KeyFromVirtualKey(keyCode);
            _awaitingKey1 = false;
            Key1_Button.Content = _key1.ToString();
        }

        if (_awaitingKey2)
        {
            _key2 = KeyInterop.KeyFromVirtualKey(keyCode);
            _awaitingKey2 = false;
            Key2_Button.Content = _key2.ToString();
        }
    }
}

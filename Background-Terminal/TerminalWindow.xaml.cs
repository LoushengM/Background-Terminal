using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Background_Terminal;

public partial class TerminalWindow : Window
{
    private const int MaxCommandHistoryEntries = 500;
    private const int CursorBlinkIntervalMs = 530;

    private readonly Func<string, Task> _sendCommand;
    private readonly Func<Task> _sendInterrupt;
    private readonly Action _terminalWindowUiUpdate;
    private readonly List<string> _commandHistory = [];

    private int _commandHistoryIndex = -1;
    private bool _locked;
    private Brush _userBackground = new SolidColorBrush(Color.FromArgb(0xD9, 0x1E, 0x1E, 0x1E));
    private double _userOpacity = 1.0;
    private DispatcherTimer? _cursorBlinkTimer;
    private bool _cursorVisible;

    public TerminalWindow(
        Func<string, Task> sendCommand,
        Func<Task> sendInterrupt,
        Action terminalWindowUiUpdate)
    {
        InitializeComponent();

        _sendCommand = sendCommand;
        _sendInterrupt = sendInterrupt;
        _terminalWindowUiUpdate = terminalWindowUiUpdate;

        Input_TextBox.GotFocus += StartCursorBlink;
        Input_TextBox.LostFocus += StopCursorBlink;
        Input_TextBox.LayoutUpdated += UpdateBlockCursor;
    }

    public void ApplyAppearance(Brush background, double opacity)
    {
        _userBackground = background;
        _userOpacity = Math.Clamp(opacity, 0.0, 1.0);
        Background = _userBackground;
        Opacity = _userOpacity;
    }

    public void SetCursorColor(Brush color)
    {
        BlockCursor.Fill = color;
    }

    public void SetWindowLocked(bool locked)
    {
        _locked = locked;

        if (locked)
        {
            ResizeMode = ResizeMode.NoResize;
            TerminalData_TextBox.IsHitTestVisible = true;
        }
        else
        {
            ResizeMode = ResizeMode.CanResizeWithGrip;
            TerminalData_TextBox.IsHitTestVisible = false;
        }
    }

    private void UpdateTerminalDataTextBoxMargin()
    {
        TerminalData_TextBox.Margin = new Thickness(0, 0, 0, Input_TextBox.ActualHeight);
    }

    private void TerminalWindow_MouseDown(object sender, MouseEventArgs e)
    {
        if (!_locked && e.LeftButton == MouseButtonState.Pressed)
        {
            DragMove();
        }
    }

    private void TerminalWindow_LocationChanged(object sender, EventArgs e)
    {
        UpdateTerminalDataTextBoxMargin();
        _terminalWindowUiUpdate();
    }

    private void TerminalWindow_SizeChanged(object sender, RoutedEventArgs e)
    {
        UpdateTerminalDataTextBoxMargin();
        _terminalWindowUiUpdate();
    }

    private void TerminalDataTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        TerminalData_TextBox.ScrollToEnd();
    }

    private async void InputTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
        {
            Input_TextBox.Clear();
            e.Handled = true;
            await _sendInterrupt();
            return;
        }

        if (e.Key == Key.Up)
        {
            if (_commandHistoryIndex + 1 < _commandHistory.Count)
            {
                _commandHistoryIndex++;
                SetInputText(_commandHistory[_commandHistoryIndex]);
            }

            e.Handled = true;
            return;
        }

        if (e.Key == Key.Down)
        {
            if (_commandHistoryIndex > 0)
            {
                _commandHistoryIndex--;
                SetInputText(_commandHistory[_commandHistoryIndex]);
            }
            else if (_commandHistoryIndex == 0)
            {
                _commandHistoryIndex = -1;
                Input_TextBox.Clear();
            }

            e.Handled = true;
            return;
        }

        if (e.Key is not (Key.Return or Key.Enter))
        {
            return;
        }

        string command = Input_TextBox.Text;
        Input_TextBox.Clear();
        e.Handled = true;

        if (!string.IsNullOrEmpty(command))
        {
            _commandHistory.Insert(0, command);
            if (_commandHistory.Count > MaxCommandHistoryEntries)
            {
                _commandHistory.RemoveAt(_commandHistory.Count - 1);
            }
        }

        _commandHistoryIndex = -1;
        await _sendCommand(command);
    }

    private void TerminalWindow_Loaded(object sender, EventArgs e)
    {
        UpdateTerminalDataTextBoxMargin();
    }

    private void SetInputText(string text)
    {
        Input_TextBox.Text = text;
        Input_TextBox.CaretIndex = text.Length;
    }

    private void StartCursorBlink(object? sender, RoutedEventArgs e)
    {
        if (_cursorBlinkTimer is not null)
        {
            return;
        }

        _cursorVisible = true;
        _cursorBlinkTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(CursorBlinkIntervalMs),
            DispatcherPriority.Normal,
            CursorBlinkTick,
            Dispatcher);
        _cursorBlinkTimer.Start();
        UpdateBlockCursor(this, EventArgs.Empty);
    }

    private void StopCursorBlink(object? sender, RoutedEventArgs e)
    {
        if (_cursorBlinkTimer is not null)
        {
            _cursorBlinkTimer.Stop();
            _cursorBlinkTimer = null;
        }

        _cursorVisible = false;
        BlockCursor.Visibility = Visibility.Collapsed;
    }

    private void CursorBlinkTick(object? sender, EventArgs e)
    {
        _cursorVisible = !_cursorVisible;
        UpdateBlockCursor(this, EventArgs.Empty);
    }

    private void UpdateBlockCursor(object? sender, EventArgs e)
    {
        if (_cursorBlinkTimer is null)
        {
            return;
        }

        int caretIndex = Input_TextBox.CaretIndex;
        Rect rect = Input_TextBox.GetRectFromCharacterIndex(caretIndex);
        Point textBoxOffset = Input_TextBox.TranslatePoint(
            new Point(0, 0), (UIElement)Input_TextBox.Parent);
        BlockCursor.Margin = new Thickness(
            textBoxOffset.X + rect.X,
            textBoxOffset.Y + rect.Y,
            0,
            0);
        BlockCursor.Width = Math.Max(rect.Width, 2);
        BlockCursor.Height = rect.Height;
        BlockCursor.Visibility = _cursorVisible
            ? Visibility.Visible
            : Visibility.Collapsed;
    }
}

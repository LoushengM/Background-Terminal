using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace Background_Terminal;

public partial class TerminalWindow : Window
{
    private const int MaxCommandHistoryEntries = 500;
    private const double ResizeHitTestThickness = 8.0;
    private const int WmNcHitTest = 0x0084;
    private const int WmEnterSizeMove = 0x0231;
    private const int WmExitSizeMove = 0x0232;
    private const int HtLeft = 10;
    private const int HtRight = 11;
    private const int HtTop = 12;
    private const int HtTopLeft = 13;
    private const int HtTopRight = 14;
    private const int HtBottom = 15;
    private const int HtBottomLeft = 16;
    private const int HtBottomRight = 17;

    private readonly Func<string, Task> _sendCommand;
    private readonly Func<Task> _sendInterrupt;
    private readonly Action _terminalWindowUiUpdate;
    private readonly List<string> _commandHistory = [];

    private int _commandHistoryIndex = -1;
    private bool _locked;
    private bool _inputActive;
    private bool _isMovingOrResizing;
    private bool _restoreInputAfterMoveOrResize;
    private HwndSource? _hwndSource;
    private Brush _userBackground = new SolidColorBrush(Color.FromArgb(0xD9, 0x1E, 0x1E, 0x1E));
    private double _userOpacity = 1.0;
    private Brush _cursorBrush = Brushes.White;
    private CursorAdorner? _cursorAdorner;
    private AdornerLayer? _cursorAdornerLayer;

    public TerminalWindow(
        Func<string, Task> sendCommand,
        Func<Task> sendInterrupt,
        Action terminalWindowUiUpdate)
    {
        InitializeComponent();

        _sendCommand = sendCommand;
        _sendInterrupt = sendInterrupt;
        _terminalWindowUiUpdate = terminalWindowUiUpdate;

        Deactivated += TerminalWindow_Deactivated;
        SourceInitialized += TerminalWindow_SourceInitialized;
        Closed += TerminalWindow_Closed;
    }

    public void ApplyAppearance(Brush background, double opacity)
    {
        _userBackground = background;
        _userOpacity = Math.Clamp(opacity, 0.0, 1.0);
        Background = _userBackground;
        Opacity = _userOpacity;
    }

    public void SetCursorColor(Brush brush)
    {
        _cursorBrush = brush;
        Input_TextBox.CaretBrush = Brushes.Transparent;
        _cursorAdorner?.SetColor(_cursorBrush);
    }

    public void ActivateInput()
    {
        bool wasInputActive = _inputActive;
        _inputActive = true;
        Input_TextBox.Focusable = true;
        _cursorAdorner?.Activate();
        RestoreInputFocusIfActive();

        if (!wasInputActive)
        {
            InputActivated?.Invoke(this, EventArgs.Empty);
        }
    }

    public void DeactivateInput()
    {
        bool wasInputActive = _inputActive;
        _inputActive = false;
        _cursorAdorner?.Deactivate();
        if (Input_TextBox.IsKeyboardFocusWithin)
        {
            Keyboard.ClearFocus();
        }

        Input_TextBox.Focusable = false;

        if (wasInputActive)
        {
            InputDeactivated?.Invoke(this, EventArgs.Empty);
        }
    }

    private void RestoreInputFocusIfActive()
    {
        if (!_inputActive)
        {
            return;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (!_inputActive || !IsActive)
            {
                return;
            }

            Input_TextBox.Focusable = true;
            FocusManager.SetFocusedElement(this, Input_TextBox);
            Keyboard.Focus(Input_TextBox);
            _cursorAdorner?.Activate();
        });
    }

    private void TerminalWindow_Deactivated(object? sender, EventArgs e)
    {
        if (_isMovingOrResizing)
        {
            _restoreInputAfterMoveOrResize |= _inputActive;
            _cursorAdorner?.Deactivate();
            return;
        }

        DeactivateInput();
    }

    private void TerminalWindow_Closed(object? sender, EventArgs e)
    {
        _hwndSource?.RemoveHook(TerminalWindow_WndProc);
        _cursorAdorner?.Dispose();
    }

    private void TerminalWindow_SourceInitialized(object? sender, EventArgs e)
    {
        _hwndSource = (HwndSource?)PresentationSource.FromVisual(this);
        _hwndSource?.AddHook(TerminalWindow_WndProc);
    }

    private IntPtr TerminalWindow_WndProc(
        IntPtr hwnd,
        int msg,
        IntPtr wParam,
        IntPtr lParam,
        ref bool handled)
    {
        if (msg == WmNcHitTest && TryGetResizeHitTest(lParam, out int hitTest))
        {
            handled = true;
            return new IntPtr(hitTest);
        }

        if (msg == WmEnterSizeMove)
        {
            BeginMoveOrResizeInteraction();
        }
        else if (msg == WmExitSizeMove)
        {
            CompleteMoveOrResizeInteraction();
        }

        return IntPtr.Zero;
    }

    private bool TryGetResizeHitTest(IntPtr lParam, out int hitTest)
    {
        hitTest = 0;

        if (_locked || ResizeMode == ResizeMode.NoResize)
        {
            return false;
        }

        long packedPoint = lParam.ToInt64();
        Point screenPoint = new(
            unchecked((short)(packedPoint & 0xffff)),
            unchecked((short)((packedPoint >> 16) & 0xffff)));
        Point windowPoint = PointFromScreen(screenPoint);

        bool isLeft = windowPoint.X >= 0 &&
            windowPoint.X < ResizeHitTestThickness;
        bool isRight = windowPoint.X <= ActualWidth &&
            windowPoint.X > ActualWidth - ResizeHitTestThickness;
        bool isTop = windowPoint.Y >= 0 &&
            windowPoint.Y < ResizeHitTestThickness;
        bool isBottom = windowPoint.Y <= ActualHeight &&
            windowPoint.Y > ActualHeight - ResizeHitTestThickness;

        hitTest = (isLeft, isRight, isTop, isBottom) switch
        {
            (true, false, true, false) => HtTopLeft,
            (false, true, true, false) => HtTopRight,
            (true, false, false, true) => HtBottomLeft,
            (false, true, false, true) => HtBottomRight,
            (true, false, false, false) => HtLeft,
            (false, true, false, false) => HtRight,
            (false, false, true, false) => HtTop,
            (false, false, false, true) => HtBottom,
            _ => 0
        };

        return hitTest != 0;
    }

    private void BeginMoveOrResizeInteraction()
    {
        _restoreInputAfterMoveOrResize |= _inputActive;
        _isMovingOrResizing = true;
    }

    private void CompleteMoveOrResizeInteraction()
    {
        if (!_isMovingOrResizing)
        {
            return;
        }

        bool restoreInput = _restoreInputAfterMoveOrResize;
        _isMovingOrResizing = false;
        _restoreInputAfterMoveOrResize = false;

        if (restoreInput)
        {
            ActivateInput();
        }
        else
        {
            _cursorAdorner?.Refresh();
        }

        RefreshCursorAfterLayout(rebuildAdorner: true);
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

        RefreshCursorAfterLayout(rebuildAdorner: true);
    }

    public void RefreshCursorAfterWindowLock()
    {
        RefreshCursorAfterLayout(rebuildAdorner: true);
    }

    private void UpdateTerminalDataTextBoxMargin()
    {
        TerminalData_TextBox.Margin = new Thickness(0, 0, 0, Input_TextBox.ActualHeight);
    }

    private void TerminalWindow_MouseDown(object sender, MouseEventArgs e)
    {
        if (!_locked && e.LeftButton == MouseButtonState.Pressed)
        {
            BeginMoveOrResizeInteraction();
            try
            {
                DragMove();
            }
            finally
            {
                CompleteMoveOrResizeInteraction();
            }
        }
    }

    private void TerminalWindow_LocationChanged(object sender, EventArgs e)
    {
        UpdateTerminalDataTextBoxMargin();
        _terminalWindowUiUpdate();
        RefreshCursorAfterLayout();
        RestoreInputFocusIfActive();
    }

    private void TerminalWindow_SizeChanged(object sender, RoutedEventArgs e)
    {
        UpdateTerminalDataTextBoxMargin();
        _terminalWindowUiUpdate();
        RefreshCursorAfterLayout();
        RestoreInputFocusIfActive();
    }

    private void TerminalDataTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        TerminalData_TextBox.ScrollToEnd();
    }

    public event EventHandler? InputActivated;
    public event EventHandler? InputDeactivated;

    private void InputTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (Input_TextBox.IsKeyboardFocusWithin)
        {
            return;
        }

        ActivateInput();
        Keyboard.Focus(Input_TextBox);
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

        RebuildCursorAdorner();
    }

    private void SetInputText(string text)
    {
        Input_TextBox.Text = text;
        Input_TextBox.CaretIndex = text.Length;
    }

    private void RefreshCursorAfterLayout(bool rebuildAdorner = false)
    {
        Dispatcher.BeginInvoke(
            () =>
            {
                Input_TextBox.UpdateLayout();
                if (rebuildAdorner)
                {
                    RebuildCursorAdorner();
                }
                else
                {
                    _cursorAdorner?.Refresh();
                }

                RestoreInputFocusIfActive();
            },
            DispatcherPriority.Render);
    }

    private void RebuildCursorAdorner()
    {
        bool wasActive = _inputActive;

        if (_cursorAdorner is not null)
        {
            _cursorAdornerLayer?.Remove(_cursorAdorner);
            _cursorAdorner.Dispose();
            _cursorAdorner = null;
        }

        _cursorAdornerLayer = AdornerLayer.GetAdornerLayer(Input_TextBox);
        if (_cursorAdornerLayer is null)
        {
            return;
        }

        _cursorAdorner = new CursorAdorner(Input_TextBox);
        _cursorAdorner.SetColor(_cursorBrush);
        _cursorAdornerLayer.Add(_cursorAdorner);

        if (wasActive)
        {
            _cursorAdorner.Activate();
        }
    }
}

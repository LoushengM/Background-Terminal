using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Threading;

namespace Background_Terminal;

public sealed class CursorAdorner : Adorner
{
    private readonly TextBox _textBox;
    private readonly DispatcherTimer _blinkTimer;
    private bool _isActive;
    private bool _blinkVisible;
    private Brush _cursorBrush = Brushes.White;

    public CursorAdorner(TextBox textBox) : base(textBox)
    {
        _textBox = textBox;

        _blinkTimer = new DispatcherTimer(
            TimeSpan.FromMilliseconds(530),
            DispatcherPriority.Render,
            OnBlinkTick,
            textBox.Dispatcher);
        _blinkTimer.Stop();

        textBox.TextChanged += OnTextBoxChanged;
        textBox.SelectionChanged += OnTextBoxChanged;
    }

    public void SetColor(Brush brush)
    {
        _cursorBrush = brush;
        InvalidateVisual();
    }

    public void Activate()
    {
        _isActive = true;
        _blinkVisible = true;
        _blinkTimer.Stop();
        _blinkTimer.Start();
        InvalidateVisual();
    }

    public void Deactivate()
    {
        _isActive = false;
        _blinkVisible = false;
        _blinkTimer.Stop();
        InvalidateVisual();
    }

    public void Refresh()
    {
        if (_isActive)
        {
            _blinkVisible = true;
            InvalidateVisual();
        }
    }

    private void OnTextBoxChanged(object sender, RoutedEventArgs e)
    {
        if (!_isActive)
        {
            return;
        }

        _blinkVisible = true;
        _blinkTimer.Stop();
        _blinkTimer.Start();
        InvalidateVisual();
    }

    private void OnBlinkTick(object? sender, EventArgs e)
    {
        _blinkVisible = !_blinkVisible;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        base.OnRender(drawingContext);

        if (!_isActive || !_blinkVisible)
        {
            return;
        }

        int caretIndex = _textBox.CaretIndex;
        Rect rect = _textBox.GetRectFromCharacterIndex(caretIndex);
        if (rect.IsEmpty ||
            !double.IsFinite(rect.X) ||
            !double.IsFinite(rect.Y) ||
            !double.IsFinite(rect.Width) ||
            !double.IsFinite(rect.Height))
        {
            return;
        }

        if (rect.Width < 1)
        {
            rect.Width = _textBox.FontSize * 0.6;
        }
        if (rect.Height < 1)
        {
            rect.Height = _textBox.FontSize;
        }

        rect = new Rect(
            Math.Floor(rect.X),
            Math.Floor(rect.Y),
            Math.Ceiling(rect.Width),
            Math.Ceiling(rect.Height));

        drawingContext.DrawRectangle(_cursorBrush, null, rect);
    }

    public void Dispose()
    {
        _blinkTimer.Stop();
        _textBox.TextChanged -= OnTextBoxChanged;
        _textBox.SelectionChanged -= OnTextBoxChanged;
    }
}

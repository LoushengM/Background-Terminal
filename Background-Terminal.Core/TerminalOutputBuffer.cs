using System.Text;

namespace Background_Terminal.Core;

public sealed class TerminalOutputBuffer
{
    private readonly object _syncRoot = new();
    private readonly Queue<string> _chunks = new();
    private int _characterCount;

    public TerminalOutputBuffer(int maxPendingCharacters)
    {
        if (maxPendingCharacters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxPendingCharacters));
        }

        MaxPendingCharacters = maxPendingCharacters;
    }

    public int MaxPendingCharacters { get; }

    public void Append(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        lock (_syncRoot)
        {
            if (text.Length >= MaxPendingCharacters)
            {
                _chunks.Clear();
                string tail = text[^MaxPendingCharacters..];
                _chunks.Enqueue(tail);
                _characterCount = tail.Length;
                return;
            }

            _chunks.Enqueue(text);
            _characterCount += text.Length;

            while (_characterCount > MaxPendingCharacters && _chunks.Count > 0)
            {
                string oldest = _chunks.Dequeue();
                _characterCount -= oldest.Length;
            }
        }
    }

    public string Drain()
    {
        lock (_syncRoot)
        {
            if (_chunks.Count == 0)
            {
                return string.Empty;
            }

            StringBuilder result = new(_characterCount);
            while (_chunks.TryDequeue(out string? chunk))
            {
                result.Append(chunk);
            }

            _characterCount = 0;
            return result.ToString();
        }
    }
}

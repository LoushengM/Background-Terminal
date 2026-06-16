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

            int overage = _characterCount - MaxPendingCharacters;
            if (overage <= 0)
            {
                return;
            }

            List<string> retained = new(_chunks.Count);
            while (_chunks.Count > 0 && overage > 0)
            {
                string oldest = _chunks.Dequeue();
                if (oldest.Length <= overage)
                {
                    overage -= oldest.Length;
                    _characterCount -= oldest.Length;
                    continue;
                }

                retained.Add(oldest[overage..]);
                _characterCount -= overage;
                overage = 0;
                break;
            }

            while (_chunks.Count > 0)
            {
                retained.Add(_chunks.Dequeue());
            }

            _chunks.Clear();
            foreach (string chunk in retained)
            {
                _chunks.Enqueue(chunk);
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

    public void Clear()
    {
        lock (_syncRoot)
        {
            _chunks.Clear();
            _characterCount = 0;
        }
    }
}

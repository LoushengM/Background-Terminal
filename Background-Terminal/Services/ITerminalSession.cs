using System;
using System.Threading;
using System.Threading.Tasks;

namespace Background_Terminal;

public interface ITerminalSession : IAsyncDisposable
{
    event Action<string>? OutputReceived;
    event Action<int>? Exited;

    bool IsRunning { get; }
    string InputNewLine { get; }

    Task StartAsync(
        string commandLine,
        int columns = 120,
        int rows = 30,
        CancellationToken cancellationToken = default);

    Task WriteAsync(string text, CancellationToken cancellationToken = default);
    Task SendInterruptAsync();
    Task StopAsync();
}

internal interface IWorkingDirectoryTerminalSession
{
    string? WorkingDirectory { get; set; }
}

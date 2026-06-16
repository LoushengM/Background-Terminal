using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Background_Terminal;

public sealed class RedirectedProcessTerminalSession :
    ITerminalSession,
    IWorkingDirectoryTerminalSession
{
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private Process? _process;
    private Task? _monitorTask;
    private int _disposed;

    public event Action<string>? OutputReceived;
    public event Action<int>? Exited;

    public bool IsRunning => _process is { HasExited: false };
    public string InputNewLine => Environment.NewLine;
    public string? WorkingDirectory { get; set; }

    public async Task StartAsync(
        string commandLine,
        int columns = 120,
        int rows = 30,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);
        string[] arguments = ParseCommandLine(commandLine);

        await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_process is not null)
            {
                throw new InvalidOperationException("A terminal session is already running.");
            }

            ProcessStartInfo startInfo = new(arguments[0])
            {
                WorkingDirectory = ResolveWorkingDirectory(WorkingDirectory)
                    ?? Environment.GetFolderPath(
                        Environment.SpecialFolder.MyDocuments),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

            for (int index = 1; index < arguments.Length; index++)
            {
                startInfo.ArgumentList.Add(arguments[index]);
            }

            Process process = new()
            {
                StartInfo = startInfo,
                EnableRaisingEvents = true
            };

            if (!process.Start())
            {
                process.Dispose();
                throw new InvalidOperationException("The terminal process did not start.");
            }

            _process = process;
            _monitorTask = MonitorProcessAsync(process);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async Task WriteAsync(
        string text,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed != 0, this);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Process process = GetRunningProcess();
            await process.StandardInput.WriteAsync(text.AsMemory(), cancellationToken)
                .ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async Task SendInterruptAsync()
    {
        Process? process = _process;
        if (process is null || process.HasExited)
        {
            return;
        }

        // Redirected stdin cannot reliably deliver a console Ctrl+C, so
        // interrupt falls back to terminating the whole process tree.
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            return;
        }
        catch (NotSupportedException)
        {
            return;
        }

        try
        {
            await process.WaitForExitAsync().ConfigureAwait(false);
        }
        catch (InvalidOperationException)
        {
        }
    }

    public async Task StopAsync()
    {
        await _lifecycleGate.WaitAsync().ConfigureAwait(false);
        try
        {
            Process? process = _process;
            if (process is null)
            {
                return;
            }

            if (!process.HasExited)
            {
                try
                {
                    process.StandardInput.Close();
                    await process.WaitForExitAsync()
                        .WaitAsync(TimeSpan.FromMilliseconds(500))
                        .ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (InvalidOperationException)
                {
                }
            }

            if (_monitorTask is not null)
            {
                await _monitorTask.ConfigureAwait(false);
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        await StopAsync().ConfigureAwait(false);
        _writeGate.Dispose();
        _lifecycleGate.Dispose();
    }

    private async Task MonitorProcessAsync(Process process)
    {
        Task standardOutput = PumpAsync(process.StandardOutput);
        Task standardError = PumpAsync(process.StandardError);

        await process.WaitForExitAsync().ConfigureAwait(false);
        await Task.WhenAll(standardOutput, standardError).ConfigureAwait(false);
        int exitCode = process.ExitCode;

        if (ReferenceEquals(Interlocked.CompareExchange(ref _process, null, process), process))
        {
            process.Dispose();
        }

        RaiseExited(exitCode);
    }

    private async Task PumpAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        while (true)
        {
            int count = await reader.ReadAsync(buffer.AsMemory()).ConfigureAwait(false);
            if (count == 0)
            {
                return;
            }

            RaiseOutput(new string(buffer, 0, count));
        }
    }

    private Process GetRunningProcess()
    {
        Process? process = _process;
        if (process is null || process.HasExited)
        {
            throw new InvalidOperationException("No terminal session is running.");
        }

        return process;
    }

    private void RaiseOutput(string text)
    {
        foreach (Action<string> handler in
            OutputReceived?.GetInvocationList() ?? [])
        {
            try
            {
                handler(text);
            }
            catch
            {
            }
        }
    }

    private void RaiseExited(int exitCode)
    {
        foreach (Action<int> handler in Exited?.GetInvocationList() ?? [])
        {
            try
            {
                handler(exitCode);
            }
            catch
            {
            }
        }
    }

    private static string[] ParseCommandLine(string commandLine)
    {
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            throw new ArgumentException("A command line is required.", nameof(commandLine));
        }

        string trimmed = commandLine.Trim();
        if (File.Exists(trimmed))
        {
            return [trimmed];
        }

        IntPtr argumentVector = CommandLineToArgv(trimmed, out int argumentCount);
        if (argumentVector == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        try
        {
            string[] arguments = new string[argumentCount];
            for (int index = 0; index < argumentCount; index++)
            {
                IntPtr argument = Marshal.ReadIntPtr(
                    argumentVector,
                    index * IntPtr.Size);
                arguments[index] = Marshal.PtrToStringUni(argument) ?? string.Empty;
            }

            if (arguments.Length == 0 || string.IsNullOrWhiteSpace(arguments[0]))
            {
                throw new ArgumentException(
                    "The command line does not contain an executable.",
                    nameof(commandLine));
            }

            return arguments;
        }
        finally
        {
            LocalFree(argumentVector);
        }
    }

    private static string? ResolveWorkingDirectory(string? workingDirectory)
    {
        if (string.IsNullOrWhiteSpace(workingDirectory))
        {
            return null;
        }

        string expanded = Environment.ExpandEnvironmentVariables(
            workingDirectory.Trim());
        return Path.GetFullPath(expanded);
    }

    [DllImport(
        "shell32.dll",
        EntryPoint = "CommandLineToArgvW",
        CharSet = CharSet.Unicode,
        SetLastError = true)]
    private static extern IntPtr CommandLineToArgv(
        string commandLine,
        out int argumentCount);

    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);
}

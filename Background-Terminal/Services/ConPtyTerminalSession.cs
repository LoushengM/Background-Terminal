#nullable enable

using Microsoft.Win32.SafeHandles;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Background_Terminal
{
    /// <summary>
    /// Runs a process attached to a Windows pseudo console.
    /// </summary>
    public sealed class ConPtyTerminalSession : ITerminalSession
    {
        private const int ErrorBrokenPipe = 109;
        private const int ErrorNoData = 232;
        private const int ErrorOperationAborted = 995;
        private const uint ExtendedStartupInfoPresent = 0x00080000;
        private const uint CreateUnicodeEnvironment = 0x00000400;
        private const int ProcThreadAttributePseudoConsole = 0x00020016;
        private const uint StillActive = 259;

        private readonly object _syncRoot = new object();
        private readonly SemaphoreSlim _lifecycleGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _writeGate = new SemaphoreSlim(1, 1);
        private SessionState? _session;
        private int _disposeRequested;

        public event Action<string>? OutputReceived;

        public event Action<int>? Exited;

        public string InputNewLine => "\r";

        public bool IsRunning
        {
            get
            {
                lock (_syncRoot)
                {
                    return _session != null && !_session.ExitCompletion.Task.IsCompleted;
                }
            }
        }

        public async Task StartAsync(
            string commandLine,
            int columns = 120,
            int rows = 30,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(commandLine))
            {
                throw new ArgumentException("A command line is required.", nameof(commandLine));
            }

            if (columns < 1 || columns > short.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(columns), columns, "Columns must be between 1 and 32767.");
            }

            if (rows < 1 || rows > short.MaxValue)
            {
                throw new ArgumentOutOfRangeException(nameof(rows), rows, "Rows must be between 1 and 32767.");
            }

            if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            {
                throw new PlatformNotSupportedException("Windows ConPTY is only available on Windows.");
            }

            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            await _lifecycleGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                ThrowIfDisposed();

                SessionState? existing;
                lock (_syncRoot)
                {
                    existing = _session;
                }

                if (existing != null)
                {
                    if (!existing.ExitCompletion.Task.IsCompleted)
                    {
                        throw new InvalidOperationException("A terminal session is already running.");
                    }

                    await existing.Finished.Task.ConfigureAwait(false);
                }

                cancellationToken.ThrowIfCancellationRequested();

                SessionState session;
                try
                {
                    session = await Task.Run(
                        () => CreateSession(commandLine, (short)columns, (short)rows),
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (EntryPointNotFoundException exception)
                {
                    throw new PlatformNotSupportedException(
                        "Windows ConPTY requires Windows 10 version 1809 or later.",
                        exception);
                }

                lock (_syncRoot)
                {
                    _session = session;
                }

                try
                {
                    StartMonitoring(session);
                    await Task.WhenAny(
                        session.FirstOutput.Task,
                        Task.Delay(500, CancellationToken.None)).ConfigureAwait(false);
                }
                catch
                {
                    session.CloseInput();
                    _ = session.ClosePseudoConsoleAsync();
                    TryTerminateProcess(session, 1);
                    session.CloseOutput();
                    session.ProcessHandle.Dispose();

                    lock (_syncRoot)
                    {
                        if (ReferenceEquals(_session, session))
                        {
                            _session = null;
                        }
                    }

                    throw;
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        public async Task WriteAsync(string text, CancellationToken cancellationToken = default)
        {
            if (text == null)
            {
                throw new ArgumentNullException(nameof(text));
            }

            ThrowIfDisposed();
            cancellationToken.ThrowIfCancellationRequested();

            await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                SessionState session = GetRunningSession();
                byte[] bytes = Encoding.UTF8.GetBytes(text);

                if (bytes.Length == 0)
                {
                    return;
                }

                await Task.Run(
                    () => WriteAll(session, bytes, cancellationToken),
                    CancellationToken.None).ConfigureAwait(false);
            }
            finally
            {
                _writeGate.Release();
            }
        }

        public Task SendInterruptAsync()
        {
            return WriteAsync("\u0003");
        }

        public Task StopAsync()
        {
            return StopCoreAsync();
        }

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposeRequested, 1) != 0)
            {
                return;
            }

            await StopCoreAsync().ConfigureAwait(false);
        }

        private async Task StopCoreAsync()
        {
            await _lifecycleGate.WaitAsync().ConfigureAwait(false);
            try
            {
                SessionState? session;
                lock (_syncRoot)
                {
                    session = _session;
                }

                if (session == null)
                {
                    return;
                }

                session.CloseInput();

                if (!await CompletesWithinAsync(session.ExitCompletion.Task, 300).ConfigureAwait(false))
                {
                    _ = session.ClosePseudoConsoleAsync();
                }

                if (!await CompletesWithinAsync(session.ExitCompletion.Task, 2000).ConfigureAwait(false))
                {
                    TryTerminateProcess(session, 1);
                }

                if (!await CompletesWithinAsync(session.ExitCompletion.Task, 5000).ConfigureAwait(false))
                {
                    throw new TimeoutException("The terminal process did not exit after the pseudo console was closed.");
                }

                if (!await CompletesWithinAsync(session.Finished.Task, 5000).ConfigureAwait(false))
                {
                    throw new TimeoutException("The terminal session did not finish releasing its resources.");
                }
            }
            finally
            {
                _lifecycleGate.Release();
            }
        }

        private static SessionState CreateSession(string commandLine, short columns, short rows)
        {
            IReadOnlyList<string> arguments = ParseCommandLine(commandLine);
            string applicationPath = ResolveExecutable(arguments[0]);
            string normalizedCommandLine = BuildCommandLine(applicationPath, arguments);

            SafeKernelHandle? pseudoConsoleInput = null;
            SafeKernelHandle? terminalInput = null;
            SafeKernelHandle? terminalOutput = null;
            SafeKernelHandle? pseudoConsoleOutput = null;
            SafePseudoConsoleHandle? pseudoConsole = null;
            SafeKernelHandle? processHandle = null;
            SafeKernelHandle? threadHandle = null;

            try
            {
                CreatePipePair(out pseudoConsoleInput, out terminalInput);
                CreatePipePair(out terminalOutput, out pseudoConsoleOutput);

                int result = CreatePseudoConsole(
                    new Coord(columns, rows),
                    pseudoConsoleInput.DangerousGetHandle(),
                    pseudoConsoleOutput.DangerousGetHandle(),
                    0,
                    out IntPtr pseudoConsoleValue);

                if (result < 0)
                {
                    Marshal.ThrowExceptionForHR(result);
                }

                pseudoConsole = new SafePseudoConsoleHandle(pseudoConsoleValue);
                pseudoConsoleInput.Dispose();
                pseudoConsoleInput = null;
                pseudoConsoleOutput.Dispose();
                pseudoConsoleOutput = null;

                ProcessInformation processInformation = CreateAttachedProcess(
                    normalizedCommandLine,
                    pseudoConsole);

                processHandle = new SafeKernelHandle(processInformation.Process, true);
                threadHandle = new SafeKernelHandle(processInformation.Thread, true);
                threadHandle.Dispose();
                threadHandle = null;

                SessionState session = new SessionState(
                    pseudoConsole,
                    terminalInput,
                    terminalOutput,
                    processHandle);

                pseudoConsole = null;
                terminalInput = null;
                terminalOutput = null;
                processHandle = null;
                return session;
            }
            catch
            {
                threadHandle?.Dispose();
                processHandle?.Dispose();
                pseudoConsole?.Dispose();
                pseudoConsoleInput?.Dispose();
                terminalInput?.Dispose();
                terminalOutput?.Dispose();
                pseudoConsoleOutput?.Dispose();
                throw;
            }
        }

        private static ProcessInformation CreateAttachedProcess(
            string commandLine,
            SafePseudoConsoleHandle pseudoConsole)
        {
            IntPtr attributeList = IntPtr.Zero;
            IntPtr attributeListSize = IntPtr.Zero;

            try
            {
                InitializeProcThreadAttributeList(
                    IntPtr.Zero,
                    1,
                    0,
                    ref attributeListSize);

                if (attributeListSize == IntPtr.Zero)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                attributeList = Marshal.AllocHGlobal(attributeListSize);
                if (!InitializeProcThreadAttributeList(attributeList, 1, 0, ref attributeListSize))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                if (!UpdateProcThreadAttribute(
                    attributeList,
                    0,
                    new IntPtr(ProcThreadAttributePseudoConsole),
                    pseudoConsole.DangerousGetHandle(),
                    new IntPtr(IntPtr.Size),
                    IntPtr.Zero,
                    IntPtr.Zero))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                StartupInfoEx startupInfo = new StartupInfoEx();
                startupInfo.StartupInfo.Size = Marshal.SizeOf<StartupInfoEx>();
                startupInfo.AttributeList = attributeList;

                StringBuilder mutableCommandLine = new StringBuilder(commandLine, commandLine.Length + 1);
                if (!CreateProcess(
                    null,
                    mutableCommandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    false,
                    ExtendedStartupInfoPresent | CreateUnicodeEnvironment,
                    IntPtr.Zero,
                    null,
                    ref startupInfo,
                    out ProcessInformation processInformation))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }

                return processInformation;
            }
            finally
            {
                if (attributeList != IntPtr.Zero)
                {
                    DeleteProcThreadAttributeList(attributeList);
                    Marshal.FreeHGlobal(attributeList);
                }

            }
        }

        private void StartMonitoring(SessionState session)
        {
            session.OutputTask = Task.Factory.StartNew(
                () => ReadOutputLoop(session),
                CancellationToken.None,
                TaskCreationOptions.LongRunning,
                TaskScheduler.Default);

            ProcessWaitHandle processWaitHandle = new ProcessWaitHandle(session.ProcessHandle);
            session.SetProcessWaitHandle(processWaitHandle);

            RegisteredWaitHandle registeredWait = ThreadPool.RegisterWaitForSingleObject(
                processWaitHandle,
                (_, timedOut) =>
                {
                    if (!timedOut)
                    {
                        OnProcessExited(session);
                    }
                },
                null,
                Timeout.Infinite,
                true);

            session.SetRegisteredWait(registeredWait);
        }

        private void OnProcessExited(SessionState session)
        {
            int exitCode = -1;
            if (GetExitCodeProcess(session.ProcessHandle, out uint nativeExitCode)
                && nativeExitCode != StillActive)
            {
                exitCode = unchecked((int)nativeExitCode);
            }

            session.ExitCompletion.TrySetResult(exitCode);
            session.FirstOutput.TrySetResult(true);
            _ = FinishSessionAsync(session, exitCode);
        }

        private async Task FinishSessionAsync(SessionState session, int exitCode)
        {
            if (Interlocked.CompareExchange(ref session.FinishStarted, 1, 0) != 0)
            {
                await session.Finished.Task.ConfigureAwait(false);
                return;
            }

            try
            {
                session.CloseInput();

                // Fast commands can exit while their final VT output is still queued.
                // Let the active reader drain before closing the pseudo console.
                await Task.Delay(750).ConfigureAwait(false);
                await session.ClosePseudoConsoleAsync().ConfigureAwait(false);

                if (!await CompletesWithinAsync(session.OutputTask, 2000).ConfigureAwait(false))
                {
                    session.CloseOutput();
                    await CompletesWithinAsync(session.OutputTask, 1000).ConfigureAwait(false);
                }

                session.CloseOutput();
                session.UnregisterWait();
                session.CloseProcessWaitHandle();
                session.ProcessHandle.Dispose();

                lock (_syncRoot)
                {
                    if (ReferenceEquals(_session, session))
                    {
                        _session = null;
                    }
                }

                RaiseExited(exitCode);
            }
            finally
            {
                session.Finished.TrySetResult(true);
            }
        }

        private void ReadOutputLoop(SessionState session)
        {
            byte[] buffer = new byte[4096];
            char[] characters = new char[Encoding.UTF8.GetMaxCharCount(buffer.Length)];
            Decoder decoder = new UTF8Encoding(false, false).GetDecoder();

            try
            {
                while (true)
                {
                    if (!ReadFile(
                        session.OutputHandle,
                        buffer,
                        (uint)buffer.Length,
                        out uint bytesRead,
                        IntPtr.Zero))
                    {
                        int error = Marshal.GetLastWin32Error();
                        if (error == ErrorBrokenPipe
                            || error == ErrorNoData
                            || error == ErrorOperationAborted)
                        {
                            break;
                        }

                        RaiseOutput(
                            $"{Environment.NewLine}[ConPTY output read failed: " +
                            $"{new Win32Exception(error).Message} ({error})]" +
                            Environment.NewLine);
                        break;
                    }

                    if (bytesRead == 0)
                    {
                        break;
                    }

                    int characterCount = decoder.GetChars(
                        buffer,
                        0,
                        checked((int)bytesRead),
                        characters,
                        0,
                        false);

                    if (characterCount > 0)
                    {
                        session.FirstOutput.TrySetResult(true);
                        RaiseOutput(new string(characters, 0, characterCount));
                    }
                }

                int remainingCharacters = decoder.GetChars(
                    Array.Empty<byte>(),
                    0,
                    0,
                    characters,
                    0,
                    true);

                if (remainingCharacters > 0)
                {
                    RaiseOutput(new string(characters, 0, remainingCharacters));
                }
            }
            catch (ObjectDisposedException)
            {
                // Expected when a stop closes the pipe while the reader is active.
            }
        }

        private static void WriteAll(
            SessionState session,
            byte[] bytes,
            CancellationToken cancellationToken)
        {
            int offset = 0;

            while (offset < bytes.Length)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int count = Math.Min(4096, bytes.Length - offset);
                byte[] chunk;
                if (offset == 0 && count == bytes.Length)
                {
                    chunk = bytes;
                }
                else
                {
                    chunk = new byte[count];
                    Buffer.BlockCopy(bytes, offset, chunk, 0, count);
                }

                if (!WriteFile(
                    session.InputHandle,
                    chunk,
                    (uint)count,
                    out uint bytesWritten,
                    IntPtr.Zero))
                {
                    int error = Marshal.GetLastWin32Error();
                    throw new IOException("Writing to the terminal input pipe failed.", new Win32Exception(error));
                }

                if (bytesWritten == 0)
                {
                    throw new IOException("The terminal input pipe accepted no data.");
                }

                offset += checked((int)bytesWritten);
            }
        }

        private SessionState GetRunningSession()
        {
            lock (_syncRoot)
            {
                if (_session == null || _session.ExitCompletion.Task.IsCompleted)
                {
                    throw new InvalidOperationException("No terminal session is running.");
                }

                return _session;
            }
        }

        private static async Task<bool> CompletesWithinAsync(Task task, int milliseconds)
        {
            if (task.IsCompleted)
            {
                await task.ConfigureAwait(false);
                return true;
            }

            Task completed = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);
            if (!ReferenceEquals(completed, task))
            {
                return false;
            }

            await task.ConfigureAwait(false);
            return true;
        }

        private static void TryTerminateProcess(SessionState session, uint exitCode)
        {
            if (session.ExitCompletion.Task.IsCompleted || session.ProcessHandle.IsClosed)
            {
                return;
            }

            if (!TerminateProcess(session.ProcessHandle, exitCode))
            {
                int error = Marshal.GetLastWin32Error();
                if (!session.ExitCompletion.Task.IsCompleted)
                {
                    throw new Win32Exception(error);
                }
            }
        }

        private void RaiseOutput(string output)
        {
            Action<string>? handlers = OutputReceived;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<string> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(output);
                }
                catch
                {
                    // A subscriber must not stop the terminal's output pump.
                }
            }
        }

        private void RaiseExited(int exitCode)
        {
            Action<int>? handlers = Exited;
            if (handlers == null)
            {
                return;
            }

            foreach (Action<int> handler in handlers.GetInvocationList())
            {
                try
                {
                    handler(exitCode);
                }
                catch
                {
                    // Process cleanup must complete even if a subscriber fails.
                }
            }
        }

        private void ThrowIfDisposed()
        {
            if (Volatile.Read(ref _disposeRequested) != 0)
            {
                throw new ObjectDisposedException(nameof(ConPtyTerminalSession));
            }
        }

        private static IReadOnlyList<string> ParseCommandLine(string commandLine)
        {
            string trimmedCommandLine = commandLine.Trim();
            if (File.Exists(trimmedCommandLine))
            {
                return new[] { trimmedCommandLine };
            }

            IntPtr argumentVector = CommandLineToArgv(commandLine, out int argumentCount);
            if (argumentVector == IntPtr.Zero)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            try
            {
                if (argumentCount == 0)
                {
                    throw new ArgumentException("The command line does not contain an executable.", nameof(commandLine));
                }

                string[] arguments = new string[argumentCount];
                for (int index = 0; index < argumentCount; index++)
                {
                    IntPtr argument = Marshal.ReadIntPtr(argumentVector, index * IntPtr.Size);
                    arguments[index] = Marshal.PtrToStringUni(argument) ?? string.Empty;
                }

                if (string.IsNullOrWhiteSpace(arguments[0]))
                {
                    throw new ArgumentException("The command line does not contain an executable.", nameof(commandLine));
                }

                return arguments;
            }
            finally
            {
                LocalFree(argumentVector);
            }
        }

        private static string ResolveExecutable(string executable)
        {
            bool containsDirectory = Path.IsPathRooted(executable)
                || executable.IndexOf(Path.DirectorySeparatorChar) >= 0
                || executable.IndexOf(Path.AltDirectorySeparatorChar) >= 0;

            if (containsDirectory)
            {
                string fullPath = Path.GetFullPath(executable);
                if (File.Exists(fullPath))
                {
                    return fullPath;
                }

                if (!Path.HasExtension(fullPath))
                {
                    string executablePath = fullPath + ".exe";
                    if (File.Exists(executablePath))
                    {
                        return executablePath;
                    }
                }

                throw new FileNotFoundException("The terminal executable was not found.", fullPath);
            }

            StringBuilder path = new StringBuilder(1024);
            uint length = SearchPath(
                null,
                executable,
                ".exe",
                (uint)path.Capacity,
                path,
                IntPtr.Zero);

            if (length == 0)
            {
                throw new FileNotFoundException(
                    "The terminal executable could not be found on the Windows search path.",
                    executable);
            }

            if (length >= path.Capacity)
            {
                path = new StringBuilder(checked((int)length + 1));
                length = SearchPath(
                    null,
                    executable,
                    ".exe",
                    (uint)path.Capacity,
                    path,
                    IntPtr.Zero);

                if (length == 0 || length >= path.Capacity)
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                }
            }

            return path.ToString();
        }

        private static string BuildCommandLine(
            string applicationPath,
            IReadOnlyList<string> parsedArguments)
        {
            StringBuilder commandLine = new StringBuilder();
            commandLine.Append(QuoteArgument(applicationPath));

            for (int index = 1; index < parsedArguments.Count; index++)
            {
                commandLine.Append(' ');
                commandLine.Append(QuoteArgument(parsedArguments[index]));
            }

            return commandLine.ToString();
        }

        private static string QuoteArgument(string argument)
        {
            if (argument.Length > 0
                && argument.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0)
            {
                return argument;
            }

            StringBuilder quoted = new StringBuilder(argument.Length + 2);
            quoted.Append('"');
            int backslashes = 0;

            foreach (char character in argument)
            {
                if (character == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (character == '"')
                {
                    quoted.Append('\\', (backslashes * 2) + 1);
                    quoted.Append('"');
                    backslashes = 0;
                    continue;
                }

                quoted.Append('\\', backslashes);
                backslashes = 0;
                quoted.Append(character);
            }

            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        private static void CreatePipePair(
            out SafeKernelHandle readHandle,
            out SafeKernelHandle writeHandle)
        {
            if (!CreatePipe(out IntPtr readValue, out IntPtr writeValue, IntPtr.Zero, 0))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error());
            }

            readHandle = new SafeKernelHandle(readValue, true);
            writeHandle = new SafeKernelHandle(writeValue, true);
        }

        private sealed class SessionState
        {
            private readonly object _resourceLock = new object();
            private Task? _closePseudoConsoleTask;
            private RegisteredWaitHandle? _registeredWait;
            private ProcessWaitHandle? _processWaitHandle;

            internal SessionState(
                SafePseudoConsoleHandle pseudoConsole,
                SafeKernelHandle inputHandle,
                SafeKernelHandle outputHandle,
                SafeKernelHandle processHandle)
            {
                PseudoConsole = pseudoConsole;
                InputHandle = inputHandle;
                OutputHandle = outputHandle;
                ProcessHandle = processHandle;
            }

            internal SafePseudoConsoleHandle PseudoConsole { get; }

            internal SafeKernelHandle InputHandle { get; }

            internal SafeKernelHandle OutputHandle { get; }

            internal SafeKernelHandle ProcessHandle { get; }

            internal Task OutputTask { get; set; } = Task.CompletedTask;

            internal TaskCompletionSource<int> ExitCompletion { get; } =
                new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource<bool> FirstOutput { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal TaskCompletionSource<bool> Finished { get; } =
                new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

            internal int FinishStarted;

            internal void CloseInput()
            {
                InputHandle.Dispose();
            }

            internal void CloseOutput()
            {
                OutputHandle.Dispose();
            }

            internal Task ClosePseudoConsoleAsync()
            {
                lock (_resourceLock)
                {
                    if (_closePseudoConsoleTask == null)
                    {
                        _closePseudoConsoleTask = Task.Run(() =>
                        {
                            PseudoConsole.Dispose();
                        });
                    }

                    return _closePseudoConsoleTask;
                }
            }

            internal void SetProcessWaitHandle(ProcessWaitHandle processWaitHandle)
            {
                lock (_resourceLock)
                {
                    _processWaitHandle = processWaitHandle;
                }
            }

            internal void SetRegisteredWait(RegisteredWaitHandle registeredWait)
            {
                lock (_resourceLock)
                {
                    if (Volatile.Read(ref FinishStarted) == 0)
                    {
                        _registeredWait = registeredWait;
                        return;
                    }
                }

                registeredWait.Unregister(null);
            }

            internal void UnregisterWait()
            {
                RegisteredWaitHandle? registeredWait;
                lock (_resourceLock)
                {
                    registeredWait = _registeredWait;
                    _registeredWait = null;
                }

                registeredWait?.Unregister(null);
            }

            internal void CloseProcessWaitHandle()
            {
                ProcessWaitHandle? processWaitHandle;
                lock (_resourceLock)
                {
                    processWaitHandle = _processWaitHandle;
                    _processWaitHandle = null;
                }

                processWaitHandle?.Dispose();
            }
        }

        private sealed class SafeKernelHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal SafeKernelHandle(IntPtr handle, bool ownsHandle)
                : base(ownsHandle)
            {
                SetHandle(handle);
            }

            protected override bool ReleaseHandle()
            {
                return CloseHandle(handle);
            }
        }

        private sealed class SafePseudoConsoleHandle : SafeHandleZeroOrMinusOneIsInvalid
        {
            internal SafePseudoConsoleHandle(IntPtr handle)
                : base(true)
            {
                SetHandle(handle);
            }

            protected override bool ReleaseHandle()
            {
                ClosePseudoConsole(handle);
                return true;
            }
        }

        private sealed class ProcessWaitHandle : WaitHandle
        {
            internal ProcessWaitHandle(SafeKernelHandle processHandle)
            {
                SafeWaitHandle = new SafeWaitHandle(processHandle.DangerousGetHandle(), false);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        private readonly struct Coord
        {
            internal Coord(short x, short y)
            {
                X = x;
                Y = y;
            }

            internal readonly short X;
            internal readonly short Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfo
        {
            internal int Size;
            private IntPtr Reserved;
            private IntPtr Desktop;
            private IntPtr Title;
            private int X;
            private int Y;
            private int XSize;
            private int YSize;
            private int XCountChars;
            private int YCountChars;
            private int FillAttribute;
            private int Flags;
            private short ShowWindow;
            private short Reserved2;
            private IntPtr Reserved2Pointer;
            private IntPtr StandardInput;
            private IntPtr StandardOutput;
            private IntPtr StandardError;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct StartupInfoEx
        {
            internal StartupInfo StartupInfo;
            internal IntPtr AttributeList;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct ProcessInformation
        {
            internal IntPtr Process;
            internal IntPtr Thread;
            private uint ProcessId;
            private uint ThreadId;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreatePipe(
            out IntPtr readPipe,
            out IntPtr writePipe,
            IntPtr pipeAttributes,
            uint size);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int CreatePseudoConsole(
            Coord size,
            IntPtr input,
            IntPtr output,
            uint flags,
            out IntPtr pseudoConsole);

        [DllImport("kernel32.dll")]
        private static extern void ClosePseudoConsole(IntPtr pseudoConsole);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool InitializeProcThreadAttributeList(
            IntPtr attributeList,
            int attributeCount,
            int flags,
            ref IntPtr size);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool UpdateProcThreadAttribute(
            IntPtr attributeList,
            uint flags,
            IntPtr attribute,
            IntPtr value,
            IntPtr size,
            IntPtr previousValue,
            IntPtr returnSize);

        [DllImport("kernel32.dll")]
        private static extern void DeleteProcThreadAttributeList(IntPtr attributeList);

        [DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool CreateProcess(
            string? applicationName,
            StringBuilder commandLine,
            IntPtr processAttributes,
            IntPtr threadAttributes,
            [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
            uint creationFlags,
            IntPtr environment,
            string? currentDirectory,
            ref StartupInfoEx startupInfo,
            out ProcessInformation processInformation);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool ReadFile(
            SafeKernelHandle file,
            [Out] byte[] buffer,
            uint bytesToRead,
            out uint bytesRead,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool WriteFile(
            SafeKernelHandle file,
            byte[] buffer,
            uint bytesToWrite,
            out uint bytesWritten,
            IntPtr overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GetExitCodeProcess(
            SafeKernelHandle process,
            out uint exitCode);

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool TerminateProcess(
            SafeKernelHandle process,
            uint exitCode);

        [DllImport("kernel32.dll", EntryPoint = "SearchPathW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint SearchPath(
            string? path,
            string fileName,
            string? extension,
            uint bufferLength,
            StringBuilder buffer,
            IntPtr filePart);

        [DllImport("shell32.dll", EntryPoint = "CommandLineToArgvW", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CommandLineToArgv(
            string commandLine,
            out int argumentCount);

        [DllImport("kernel32.dll")]
        private static extern IntPtr LocalFree(IntPtr memory);
    }
}

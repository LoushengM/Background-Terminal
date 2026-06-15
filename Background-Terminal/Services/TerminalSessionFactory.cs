using System;
using System.Text;
using System.Threading.Tasks;

namespace Background_Terminal;

public static class TerminalSessionFactory
{
    private const string ProbeMarker = "BACKGROUND_TERMINAL_CONPTY_OK";
    private static readonly Lazy<Task<bool>> ConPtyProbe = new(ProbeConPtyAsync);

    public static async Task<ITerminalSession> CreateAsync()
    {
        return await ConPtyProbe.Value.ConfigureAwait(false)
            ? new ConPtyTerminalSession()
            : new RedirectedProcessTerminalSession();
    }

    public static Task<bool> IsConPtyFunctionalAsync() => ConPtyProbe.Value;

    private static async Task<bool> ProbeConPtyAsync()
    {
        StringBuilder output = new();
        TaskCompletionSource<int> exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            await using ConPtyTerminalSession session = new();
            session.OutputReceived += text =>
            {
                lock (output)
                {
                    output.Append(text);
                }
            };
            session.Exited += exitCode => exited.TrySetResult(exitCode);

            await session.StartAsync($"cmd.exe /d /q /c echo {ProbeMarker}")
                .ConfigureAwait(false);
            await exited.Task.WaitAsync(TimeSpan.FromSeconds(4)).ConfigureAwait(false);

            lock (output)
            {
                return output.ToString().Contains(
                    ProbeMarker,
                    StringComparison.Ordinal);
            }
        }
        catch
        {
            return false;
        }
    }
}

using System.Text;

namespace Background_Terminal.IntegrationTests;

[TestClass]
public sealed class TerminalSessionFactoryTests
{
    [TestMethod]
    [Timeout(15_000)]
    public async Task SelectedBackend_StreamsCommandsAndOutput()
    {
        StringBuilder output = new();
        TaskCompletionSource<int> exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        await using ITerminalSession session =
            await TerminalSessionFactory.CreateAsync();
        session.OutputReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
            }
        };
        session.Exited += exitCode => exited.TrySetResult(exitCode);

        await session.StartAsync("cmd.exe /d /q");
        await session.WriteAsync("echo TERMINAL_BACKEND_OK" + session.InputNewLine);
        await WaitForOutputAsync(output, "TERMINAL_BACKEND_OK");
        await session.WriteAsync("exit" + session.InputNewLine);

        int exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.AreEqual(0, exitCode);
        Assert.IsFalse(session.IsRunning);
    }

    private static async Task WaitForOutputAsync(
        StringBuilder output,
        string expected)
    {
        DateTime deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            lock (output)
            {
                if (output.ToString().Contains(expected, StringComparison.Ordinal))
                {
                    return;
                }
            }

            await Task.Delay(25);
        }

        Assert.Fail($"Timed out waiting for terminal output containing '{expected}'.");
    }
}

using System.Text;

namespace Background_Terminal.IntegrationTests;

[TestClass]
public sealed class TerminalSessionCleanupTests
{
    [TestMethod]
    [Timeout(15_000)]
    public async Task ConPtyFastExit_CleansUpPromptly()
    {
        AssumeConPtyAvailable();

        await using ConPtyTerminalSession session = new();
        TaskCompletionSource<int> exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Exited += exitCode => exited.TrySetResult(exitCode);

        await session.StartAsync("cmd.exe /d /q /c exit 0");

        int exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, exitCode);

        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(session.IsRunning);
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task ConPtyStopWhileOutputDrains_Completes()
    {
        AssumeConPtyAvailable();

        await using ConPtyTerminalSession session = new();
        StringBuilder output = new();
        TaskCompletionSource<int> exited = new(
            TaskCreationOptions.RunContinuationsAsynchronously);

        session.OutputReceived += text =>
        {
            lock (output)
            {
                output.Append(text);
            }
        };

        session.Exited += exitCode => exited.TrySetResult(exitCode);

        await session.StartAsync(
            "cmd.exe /d /q /c for /l %i in (1,1,100000) do @echo LINE %i & timeout /t 3 /nobreak >nul");

        await Task.Delay(250);
        await session.StopAsync().WaitAsync(TimeSpan.FromSeconds(10));

        int exitCode = await exited.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.IsFalse(session.IsRunning);

        lock (output)
        {
            Assert.IsTrue(output.Length > 0);
        }
    }

    [TestMethod]
    [Timeout(20_000)]
    public async Task RedirectedInterrupt_TerminatesAndAllowsRestart()
    {
        await using RedirectedProcessTerminalSession session = new();
        TaskCompletionSource<int> firstExit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Exited += exitCode => firstExit.TrySetResult(exitCode);

        await session.StartAsync("cmd.exe /d /q /c timeout /t 30 /nobreak >nul");
        await session.SendInterruptAsync();

        int interruptedExitCode = await firstExit.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreNotEqual(0, interruptedExitCode);
        Assert.IsFalse(session.IsRunning);

        TaskCompletionSource<int> restartedExit = new(
            TaskCreationOptions.RunContinuationsAsynchronously);
        session.Exited += exitCode => restartedExit.TrySetResult(exitCode);

        await session.StartAsync("cmd.exe /d /q /c exit 0");

        int restartExitCode = await restartedExit.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.AreEqual(0, restartExitCode);
        Assert.IsFalse(session.IsRunning);
    }

    private static void AssumeConPtyAvailable()
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
        {
            Assert.Inconclusive("Windows ConPTY is not available on this system.");
        }
    }
}

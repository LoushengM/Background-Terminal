using Background_Terminal.Core;

namespace Background_Terminal.Core.Tests;

[TestClass]
public sealed class VirtualTerminalTextFilterTests
{
    [TestMethod]
    public void Filter_RemovesCommonCsiAndOscSequences()
    {
        VirtualTerminalTextFilter filter = new();

        string result = filter.Filter(
            "\u001b[31mred\u001b[0m \u001b]0;title\aoutput");

        Assert.AreEqual("red output", result);
    }

    [TestMethod]
    public void Filter_TracksSequencesAcrossChunks()
    {
        VirtualTerminalTextFilter filter = new();

        Assert.AreEqual("before", filter.Filter("before\u001b["));
        Assert.AreEqual("after", filter.Filter("2Jafter"));
        Assert.AreEqual(string.Empty, filter.Filter("\u001b]0;split"));
        Assert.AreEqual("done", filter.Filter("\u001b\\done"));
    }
}

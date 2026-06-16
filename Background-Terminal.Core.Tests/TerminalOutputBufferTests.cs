using Background_Terminal.Core;

namespace Background_Terminal.Core.Tests;

[TestClass]
public sealed class TerminalOutputBufferTests
{
    [TestMethod]
    public void Constructor_RejectsNonPositiveBounds()
    {
        Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            () => new TerminalOutputBuffer(0));
    }

    [TestMethod]
    public void Append_LargerThanBound_KeepsOnlyNewestCharacters()
    {
        TerminalOutputBuffer buffer = new(5);

        buffer.Append("1234567");

        Assert.AreEqual("34567", buffer.Drain());
    }

    [TestMethod]
    public void Append_OverageInsideOlderChunk_KeepsNewestTail()
    {
        TerminalOutputBuffer buffer = new(5);

        buffer.Append("abcde");
        buffer.Append("f");

        Assert.AreEqual("bcdef", buffer.Drain());
    }

    [TestMethod]
    public void Append_OneCharacterChunks_StaysBounded()
    {
        TerminalOutputBuffer buffer = new(5);

        foreach (char character in "abcdef")
        {
            buffer.Append(character.ToString());
        }

        Assert.AreEqual("bcdef", buffer.Drain());
    }

    [TestMethod]
    public void Append_TrimsWithoutSplittingSurrogatePairs()
    {
        TerminalOutputBuffer buffer = new(2);
        string smilingFace = char.ConvertFromUtf32(0x1F642);

        buffer.Append($"{smilingFace}x");

        string drained = buffer.Drain();

        Assert.AreEqual("x", drained);
    }

    [TestMethod]
    public void Drain_ReturnsPendingChunksAndClearsTheBuffer()
    {
        TerminalOutputBuffer buffer = new(20);
        buffer.Append("a");
        buffer.Append("b");
        buffer.Append(null);
        buffer.Append(string.Empty);

        Assert.AreEqual("ab", buffer.Drain());
        Assert.AreEqual(string.Empty, buffer.Drain());

        buffer.Append("c");
        Assert.AreEqual("c", buffer.Drain());
    }

    [TestMethod]
    public void Clear_RemovesPendingOutput()
    {
        TerminalOutputBuffer buffer = new(20);
        buffer.Append("pending");

        buffer.Clear();

        Assert.AreEqual(string.Empty, buffer.Drain());
    }
}

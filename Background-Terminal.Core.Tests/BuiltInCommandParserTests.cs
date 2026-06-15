using Background_Terminal.Core;

namespace Background_Terminal.Core.Tests;

[TestClass]
public sealed class BuiltInCommandParserTests
{
    [TestMethod]
    public void Parse_RequiresExactBuiltInCommandName()
    {
        Assert.AreEqual(
            BuiltInCommandKind.None,
            BuiltInCommandParser.Parse(@"bgtest newline \n").Kind);
        Assert.AreEqual(
            BuiltInCommandKind.None,
            BuiltInCommandParser.Parse("ssh-keygen example.com").Kind);

        BuiltInCommandResult result =
            BuiltInCommandParser.Parse(@"BGT NEWLINE \r\n");

        Assert.AreEqual(BuiltInCommandKind.SetNewline, result.Kind);
        Assert.AreEqual("\r\n", result.Newline);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    [DataRow("bgt")]
    [DataRow("bgt newline")]
    [DataRow(@"bgt newline \n extra")]
    [DataRow("bgt other value")]
    public void Parse_InvalidBuiltInShapes_ReturnUsageError(string command)
    {
        BuiltInCommandResult result = BuiltInCommandParser.Parse(command);

        Assert.AreEqual(BuiltInCommandKind.Invalid, result.Kind);
        Assert.IsNull(result.Newline);
        StringAssert.Contains(result.Error, "Usage:");
    }

    [TestMethod]
    [DataRow(@"bgt newline \x")]
    [DataRow("bgt newline \\")]
    public void Parse_MalformedEscapes_ReturnValidationError(string command)
    {
        BuiltInCommandResult result = BuiltInCommandParser.Parse(command);

        Assert.AreEqual(BuiltInCommandKind.Invalid, result.Kind);
        Assert.IsNull(result.Newline);
        StringAssert.Contains(result.Error, "invalid escape sequence");
    }

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("dir")]
    public void Parse_NonBuiltIns_AreIgnored(string? command)
    {
        BuiltInCommandResult result = BuiltInCommandParser.Parse(command);

        Assert.AreEqual(BuiltInCommandKind.None, result.Kind);
        Assert.IsNull(result.Newline);
        Assert.IsNull(result.Error);
    }
}

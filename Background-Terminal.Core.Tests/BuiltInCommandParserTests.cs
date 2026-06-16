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
            BuiltInCommandParser.Parse(@"BGT NEWLINE \r\n\t\\");

        Assert.AreEqual(BuiltInCommandKind.SetNewline, result.Kind);
        Assert.AreEqual("\r\n\t\\", result.Newline);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    [DataRow(@"bgt newline \r", "\r")]
    [DataRow(@"bgt newline \n", "\n")]
    [DataRow(@"bgt newline \t", "\t")]
    [DataRow(@"bgt newline \\", "\\")]
    public void Parse_SupportedEscapes_AreDecoded(
        string command,
        string expectedNewline)
    {
        BuiltInCommandResult result = BuiltInCommandParser.Parse(command);

        Assert.AreEqual(BuiltInCommandKind.SetNewline, result.Kind);
        Assert.AreEqual(expectedNewline, result.Newline);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    [DataRow("clear")]
    [DataRow("cls")]
    [DataRow("Clear-Host")]
    public void Parse_ClearCommands_ReturnClearKind(string command)
    {
        BuiltInCommandResult result = BuiltInCommandParser.Parse(command);

        Assert.AreEqual(BuiltInCommandKind.Clear, result.Kind);
        Assert.IsNull(result.Newline);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    [DataRow("clear something")]
    [DataRow("cls /?")]
    public void Parse_ClearCommandsWithArguments_AreIgnored(string command)
    {
        BuiltInCommandResult result = BuiltInCommandParser.Parse(command);

        Assert.AreEqual(BuiltInCommandKind.None, result.Kind);
        Assert.IsNull(result.Newline);
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
    [DataRow(@"bgt newline \u0041")]
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

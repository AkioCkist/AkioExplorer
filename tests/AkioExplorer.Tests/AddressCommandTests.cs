using AkioExplorer.Core.Logic;

namespace AkioExplorer.Tests;

public class AddressCommandTests
{
    [Theory]
    [InlineData("cmd", "cmd", "")]
    [InlineData("  code .  ", "code", ".")]
    [InlineData("git status --short", "git", "status --short")]
    [InlineData("\"C:\\My Tools\\x.exe\" -v \"a b\"", "C:\\My Tools\\x.exe", "-v \"a b\"")]
    [InlineData("https://example.com", "https://example.com", "")]
    public void Splits_program_and_arguments(string text, string program, string arguments)
    {
        Assert.True(AddressCommand.TryParse(text, out var p, out var a));
        Assert.Equal(program, p);
        Assert.Equal(arguments, a);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("\"unclosed")]
    [InlineData("\"\" args")]
    public void Rejects_empty_or_broken_text(string? text)
    {
        Assert.False(AddressCommand.TryParse(text, out _, out _));
    }

    [Fact]
    public void Windows_terminal_opens_in_the_folder()
    {
        Assert.Equal("-d \"D:\\Work\"", AddressCommand.TerminalArguments("wt", "", "D:\\Work"));
        Assert.Equal("-d \"C:\\\\\"", AddressCommand.TerminalArguments("wt.exe", "", "C:\\"));
        Assert.Equal("-d \"D:\\Work\" -p PowerShell", AddressCommand.TerminalArguments("WT", "-p PowerShell", "D:\\Work"));
    }

    [Fact]
    public void Explicit_terminal_folder_and_other_programs_are_left_alone()
    {
        Assert.Equal("-d C:\\Other", AddressCommand.TerminalArguments("wt", "-d C:\\Other", "D:\\Work"));
        Assert.Equal(".", AddressCommand.TerminalArguments("code", ".", "D:\\Work"));
    }
}

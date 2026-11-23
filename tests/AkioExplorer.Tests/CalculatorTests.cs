using AkioExplorer.Core.Logic;

namespace AkioExplorer.Tests;

public sealed class CalculatorTests
{
    [Theory]
    [InlineData("2+2*0.231314/2", "2.231314")]
    [InlineData("0.1+0.2", "0.3")]
    [InlineData("(1+2)*3", "9")]
    [InlineData("2^3^2", "512")]
    [InlineData("-2^2", "-4")]
    [InlineData("2**10", "1024")]
    [InlineData("10 x 3", "30")]
    [InlineData("10x3", "30")]
    [InlineData("6×7", "42")]
    [InlineData("9÷4", "2.25")]
    [InlineData("15% * 80", "12")]
    [InlineData("5!", "120")]
    [InlineData("sqrt(16)+1", "5")]
    [InlineData("sqrt(4)^2", "4")]
    [InlineData("√9*2", "6")]
    [InlineData("2pi", "6.28318530717959")]
    [InlineData("3(4+1)", "15")]
    [InlineData("(1+1)(2+2)", "8")]
    [InlineData("10 mod 3", "1")]
    [InlineData("log(1000)", "3")]
    [InlineData("1e3+1", "1001")]
    [InlineData("1/3", "0.333333333333333")]
    [InlineData("= 42", "42")]
    [InlineData("1 − 3", "-2")]
    [InlineData("10^20", "1e20")]
    public void Evaluates(string text, string expected)
    {
        Assert.True(Calculator.TryEvaluate(text, out double value));
        Assert.Equal(expected, Calculator.Format(value));
    }

    [Theory]
    [InlineData("")]
    [InlineData("42")]               // nothing computed: a file search
    [InlineData("(42)")]
    [InlineData("pi")]
    [InlineData("nay ngày bao nhiêu")]
    [InlineData("notepad")]
    [InlineData("C:\\Windows")]
    [InlineData("file-2")]
    [InlineData("2+")]
    [InlineData("(1+2")]
    [InlineData("1/0")]              // infinite
    [InlineData("sqrt(-1)")]         // NaN
    [InlineData("2 3")]
    [InlineData("2x")]
    [InlineData("10:30")]
    public void Ignores_non_arithmetic(string text) => Assert.False(Calculator.TryEvaluate(text, out _));
}

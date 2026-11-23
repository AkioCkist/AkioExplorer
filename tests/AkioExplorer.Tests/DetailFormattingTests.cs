using System.Globalization;
using AkioExplorer.Core.Logic;

namespace AkioExplorer.Tests;

public sealed class DetailFormattingTests
{
    private static readonly CultureInfo En = CultureInfo.GetCultureInfo("en-US");

    [Fact]
    public void Sizes_exact_decimal_and_binary()
    {
        Assert.Equal("7,322,333 bytes", DetailFormatting.Bytes(7_322_333, En));
        Assert.Equal("7.32 MB", DetailFormatting.Decimal(7_322_333, En));
        Assert.Equal("6.98 MiB", DetailFormatting.Binary(7_322_333, En));
        Assert.Equal("512 B", DetailFormatting.Decimal(512, En));
        Assert.Equal("4.33 GB", DetailFormatting.Decimal(4_330_000_000, En));
    }

    [Fact]
    public void Age_is_compact()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        Assert.Equal("just now", DetailFormatting.Age(now.AddSeconds(-20), now));
        Assert.Equal("13 min", DetailFormatting.Age(now.AddMinutes(-13), now));
        Assert.Equal("5 h", DetailFormatting.Age(now.AddHours(-5), now));
        Assert.Equal("3 days", DetailFormatting.Age(now.AddDays(-3), now));
        Assert.Equal("2 months", DetailFormatting.Age(now.AddMonths(-2), now));
        Assert.Equal("1 year 3 months", DetailFormatting.Age(now.AddMonths(-15), now));
    }

    [Fact]
    public void Size_summary_is_one_line()
    {
        Assert.Equal("512 bytes", DetailFormatting.SizeSummary(512, En));
        Assert.Equal($"{Formatting.FormatSize(34_112_334, En)} (34,112,334 bytes)", DetailFormatting.SizeSummary(34_112_334, En));
    }

    [Fact]
    public void Date_with_age()
    {
        var now = new DateTimeOffset(2026, 11, 22, 12, 0, 0, TimeSpan.Zero);
        var when = now.AddDays(-27);
        string date = when.LocalDateTime.ToString("g", En);
        Assert.Equal($"{date} · 27 days ago", DetailFormatting.DateWithAge(when, now, En));
        Assert.EndsWith(" · just now", DetailFormatting.DateWithAge(now.AddSeconds(-5), now, En));
    }

    [Fact]
    public void Attribute_names_skip_the_noise()
    {
        Assert.Null(DetailFormatting.AttributeNames(FileAttributes.Archive));
        Assert.Null(DetailFormatting.AttributeNames(FileAttributes.Normal));
        Assert.Equal("Read-only, Hidden", DetailFormatting.AttributeNames(FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.Archive));
    }

    [Fact]
    public void Duration_attributes_bitrate()
    {
        Assert.Equal("2:57", DetailFormatting.Duration(TimeSpan.FromSeconds(177)));
        Assert.Equal("1:02:03", DetailFormatting.Duration(new TimeSpan(1, 2, 3)));
        Assert.Equal("-a----", DetailFormatting.Attributes(FileAttributes.Archive));
        Assert.Equal("ra-s--", DetailFormatting.Attributes(FileAttributes.ReadOnly | FileAttributes.Archive | FileAttributes.System));
        Assert.Equal("320 kbps", DetailFormatting.BitRate(320_000));
        Assert.Equal("12.5 Mbps", DetailFormatting.BitRate(12_500_000));
    }
}

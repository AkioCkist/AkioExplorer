using System.Text;
using AkioExplorer.Core.Logic;
using AkioExplorer.Core.Models;

namespace AkioExplorer.Tests;

public sealed class PreviewClassifierTests
{
    private static StorageItem File(string name, long size = 1000) =>
        new() { Name = name, Path = name, Kind = StorageItemKind.File, Size = size };

    [Theory]
    [InlineData("photo.JPG", true, PreviewKind.Image)]
    [InlineData("logo.svg", false, PreviewKind.Svg)]
    [InlineData("paper.pdf", false, PreviewKind.Pdf)]
    [InlineData("clip.mp4", true, PreviewKind.Media)]
    [InlineData("clip.mp4", false, PreviewKind.None)]
    [InlineData("Program.cs", false, PreviewKind.Text)]
    [InlineData("Dockerfile", true, PreviewKind.Text)]
    [InlineData("report.docx", true, PreviewKind.ShellThumbnail)]
    [InlineData("report.docx", false, PreviewKind.None)]
    [InlineData("archive.rar", true, PreviewKind.ShellThumbnail)]
    [InlineData("archive.rar", false, PreviewKind.None)]
    public void Classifies_by_extension_and_location(string name, bool local, PreviewKind expected) =>
        Assert.Equal(expected, PreviewClassifier.Classify(File(name), local));

    [Fact]
    public void Folders_have_no_content_preview() =>
        Assert.Equal(PreviewKind.None, PreviewClassifier.Classify(new StorageItem { Name = "a", Path = "a", Kind = StorageItemKind.Folder }, true));

    [Fact]
    public void Large_cloud_images_are_not_downloaded() =>
        Assert.Equal(PreviewKind.None, PreviewClassifier.Classify(File("big.png", PreviewClassifier.CloudImageLimit + 1), false));

    [Fact]
    public void Decodes_utf8_and_rejects_binary()
    {
        Assert.True(PreviewClassifier.TryDecodeText(Encoding.UTF8.GetBytes("Xin chào\nthế giới"), out var text));
        Assert.Equal("Xin chào\nthế giới", text);
        Assert.False(PreviewClassifier.TryDecodeText(new byte[] { 0x50, 0x4B, 0x03, 0x04, 0x00, 0x14 }, out _));
    }

    [Fact]
    public void Tolerates_a_character_cut_by_the_prefix_limit()
    {
        byte[] bytes = Encoding.UTF8.GetBytes("abc thế");
        Assert.True(PreviewClassifier.TryDecodeText(bytes.AsSpan(0, bytes.Length - 1), out var text));
        Assert.Equal("abc th", text);
    }

    [Fact]
    public void Decodes_utf16_with_bom()
    {
        byte[] bytes = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("hello")];
        Assert.True(PreviewClassifier.TryDecodeText(bytes, out var text));
        Assert.Equal("hello", text);
    }

    [Fact]
    public async Task Reads_only_the_prefix()
    {
        using var stream = new MemoryStream(new byte[100]);
        Assert.Equal(10, (await PreviewClassifier.ReadPrefixAsync(stream, 10, CancellationToken.None)).Length);
        using var small = new MemoryStream(new byte[3]);
        Assert.Equal(3, (await PreviewClassifier.ReadPrefixAsync(small, 10, CancellationToken.None)).Length);
    }
}

using System.Text;
using AkioExplorer.Infrastructure.Local;

namespace AkioExplorer.Tests;

public class RecycleBinTests
{
    [Fact]
    public void Reads_windows_10_info_files()
    {
        const string path = @"C:\Users\me\Documents\report.docx";
        var when = new DateTimeOffset(2026, 11, 22, 10, 30, 0, TimeSpan.Zero);
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(2L));
        bytes.AddRange(BitConverter.GetBytes(12345L));
        bytes.AddRange(BitConverter.GetBytes(when.ToFileTime()));
        bytes.AddRange(BitConverter.GetBytes(path.Length + 1));
        bytes.AddRange(Encoding.Unicode.GetBytes(path + "\0"));

        var entry = RecycleBin.Parse(bytes.ToArray());

        Assert.NotNull(entry);
        Assert.Equal(path, entry!.OriginalPath);
        Assert.Equal(12345, entry.Size);
        Assert.Equal(when, entry.Deleted);
    }

    [Fact]
    public void Reads_version_1_info_files()
    {
        const string path = @"D:\old.txt";
        var bytes = new byte[24 + 520];
        BitConverter.GetBytes(1L).CopyTo(bytes, 0);
        BitConverter.GetBytes(7L).CopyTo(bytes, 8);
        Encoding.Unicode.GetBytes(path).CopyTo(bytes, 24);

        var entry = RecycleBin.Parse(bytes);

        Assert.Equal(path, entry!.OriginalPath);
        Assert.Equal(7, entry.Size);
        Assert.Null(entry.Deleted);
    }

    [Fact]
    public void Rejects_garbage() => Assert.Null(RecycleBin.Parse(new byte[] { 9, 9, 9 }));
}

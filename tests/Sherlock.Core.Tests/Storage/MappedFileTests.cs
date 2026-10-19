using System;
using System.IO;
using Sherlock.Core.Storage;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.Storage;

public sealed class MappedFileTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _path;

    public MappedFileTests() => _path = _tmp.File(".bin");

    public void Dispose() => _tmp.Dispose();

    // A deterministic pattern so any misaligned read is detectable.
    private static byte[] Pattern(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 2654435761u >> 24);
        return b;
    }

    [Fact]
    public void SpanReadsAnyRange()
    {
        byte[] data = Pattern(50_000);
        File.WriteAllBytes(_path, data);
        using var file = MappedFile.Open(_path);

        Assert.Equal(data.Length, file.Length);
        Assert.Equal(data, file.Span(0, data.Length).ToArray());
        foreach ((int offset, int length) in new[] { (0, 1), (7, 13), (1000, 4096), (49_999, 1), (12_345, 6789), (50_000, 0) })
        {
            Assert.Equal(data.AsSpan(offset, length).ToArray(), file.Span(offset, length).ToArray());
        }
    }

    [Fact]
    public void SpanRejectsReadsOutsideTheFile()
    {
        File.WriteAllBytes(_path, Pattern(1024));
        using var file = MappedFile.Open(_path);

        Assert.Throws<ArgumentOutOfRangeException>(() => file.Span(1000, 100).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => file.Span(-1, 1).ToArray());
        Assert.Throws<ArgumentOutOfRangeException>(() => file.Span(long.MaxValue, 1).ToArray());
    }

    [Fact]
    public void OpenRejectsAnEmptyFile()
    {
        File.WriteAllBytes(_path, []);
        Assert.Throws<InvalidDataException>(() => MappedFile.Open(_path));
    }

    [Fact]
    public void ReadsAfterDisposeThrow()
    {
        File.WriteAllBytes(_path, Pattern(16));
        var file = MappedFile.Open(_path);
        file.Dispose();

        Assert.Throws<ObjectDisposedException>(() => file.Span(0, 1).ToArray());
    }

    [Fact]
    public void ReadsPastFourGigabytesWithoutCopying()
    {
        if (OperatingSystem.IsWindows())
        {
            Assert.Skip("NTFS fills the gap with zeros on disk; the sparse file this needs is macOS and Linux only.");
        }
        const long offset = (5L << 30) + 123;
        using (var stream = new FileStream(_path, FileMode.Create))
        {
            stream.SetLength(6L << 30);
            stream.Position = offset;
            stream.Write([1, 2, 3, 4]);
        }
        using var file = MappedFile.Open(_path);

        Assert.Equal(6L << 30, file.Length);
        Assert.Equal(new byte[] { 0, 1, 2, 3, 4, 0 }, file.Span(offset - 1, 6).ToArray());
    }
}

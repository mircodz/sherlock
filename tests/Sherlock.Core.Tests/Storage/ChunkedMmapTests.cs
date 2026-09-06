using System;
using System.IO;
using Sherlock.Core.Storage;
using Sherlock.Core.Tests.Common;
using Xunit;

namespace Sherlock.Core.Tests.Storage;

public sealed class ChunkedMmapTests : IDisposable
{
    private readonly TempDir _tmp = new();
    private readonly string _path;

    public ChunkedMmapTests() => _path = _tmp.File(".bin");

    public void Dispose() => _tmp.Dispose();

    private void Write(byte[] bytes) => File.WriteAllBytes(_path, bytes);

    // A deterministic pattern so any misaligned copy is detectable.
    private static byte[] Pattern(int n)
    {
        var b = new byte[n];
        for (int i = 0; i < n; i++) b[i] = (byte)(i * 2654435761u >> 24);
        return b;
    }

    [Fact]
    public void CopyTo_ReadsWholeFile()
    {
        byte[] data = Pattern(100_000);
        Write(data);
        using var mmap = ChunkedMmap.Open(_path);

        Assert.Equal(data.Length, mmap.Length);
        var dest = new byte[data.Length];
        mmap.CopyTo(0, dest);
        Assert.Equal(data, dest);
    }

    [Fact]
    public void CopyTo_ReadsArbitrarySubranges()
    {
        byte[] data = Pattern(50_000);
        Write(data);
        using var mmap = ChunkedMmap.Open(_path);

        foreach ((int off, int len) in new[] { (0, 1), (7, 13), (1000, 4096), (49_999, 1), (12_345, 6789) })
        {
            var dest = new byte[len];
            mmap.CopyTo(off, dest);
            Assert.Equal(data.AsSpan(off, len).ToArray(), dest);
        }
    }

    [Fact]
    public void CopyTo_RejectsOutOfBounds()
    {
        Write(Pattern(1024));
        using var mmap = ChunkedMmap.Open(_path);
        Assert.Throws<ArgumentOutOfRangeException>(() => mmap.CopyTo(1000, new byte[100]));
        Assert.Throws<ArgumentOutOfRangeException>(() => mmap.CopyTo(-1, new byte[1]));
    }

    [Fact]
    public void TryGetContiguous_ReturnsZeroCopySpanWithinChunk()
    {
        byte[] data = Pattern(10_000);
        Write(data);
        using var mmap = ChunkedMmap.Open(_path);

        Assert.True(mmap.TryGetContiguous(100, 500, out ReadOnlySpan<byte> span));
        Assert.Equal(data.AsSpan(100, 500).ToArray(), span.ToArray());

        Assert.False(mmap.TryGetContiguous(9_900, 500, out _));
    }

    [Fact]
    public void Open_RejectsEmptyFile()
    {
        Write([]);
        Assert.Throws<InvalidDataException>(() => ChunkedMmap.Open(_path));
    }
}

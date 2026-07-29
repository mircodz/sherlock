using System;
using System.IO;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Tests.Common;

/// <summary>A scratch directory that deletes itself on dispose. Hold one per test class:
/// <c>private readonly TempDir _tmp = new();</c> and implement <see cref="IDisposable"/> to dispose it.</summary>
public sealed class TempDir : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("sherlock-test").FullName;

    /// <summary>A fresh unique file path inside this directory (not created).</summary>
    public string File(string extension = ".slab") =>
        System.IO.Path.Combine(Path, $"{Guid.NewGuid():N}{extension}");

    /// <summary>Serializes a container to a temp file and opens it as a <see cref="SlabFile"/>.</summary>
    public SlabFile WriteSlab(ContainerWriter writer)
    {
        string path = File();
        writer.Save(path);
        return SlabFile.Open(path);
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch { /* best effort */ }
    }
}

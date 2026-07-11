using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace Sherlock.Core.Storage;

/// <summary>Interns frames + stacks and emits the Strings/Frames/Stacks/StackFrames sections. Managed mirror of the native <c>StackInterner</c>.</summary>
public sealed class StackTableBuilder
{
    private readonly Dictionary<string, uint> _frameIds = [];
    private readonly List<string> _frameNames = [];
    private readonly Dictionary<string, uint> _stackIds = [];
    private readonly List<uint[]> _stacks = [];

    /// <summary>Returns the id for a frame name, assigning a new one on first sight.</summary>
    public uint InternFrame(string name)
    {
        if (_frameIds.TryGetValue(name, out uint id))
        {
            return id;
        }
        id = (uint)_frameNames.Count;
        _frameIds[name] = id;
        _frameNames.Add(name);
        return id;
    }

    /// <summary>Returns the id for a frame-id sequence, deduplicating identical stacks.</summary>
    public uint InternStack(ReadOnlySpan<uint> frames)
    {
        string key = Convert.ToBase64String(MemoryMarshal.AsBytes(frames));
        if (_stackIds.TryGetValue(key, out uint id))
        {
            return id;
        }
        id = (uint)_stacks.Count;
        _stackIds[key] = id;
        _stacks.Add(frames.ToArray());
        return id;
    }

    public void WriteTo(ContainerWriter w)
    {
        var strings = new List<byte>();
        var frames = new FrameRecord[_frameNames.Count];
        for (int i = 0; i < _frameNames.Count; i++)
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(_frameNames[i]);
            frames[i] = new FrameRecord { StrOffset = (uint)strings.Count, StrLen = (uint)utf8.Length };
            strings.AddRange(utf8);
        }

        var stackRecs = new StackRecord[_stacks.Count];
        var pool = new List<uint>();
        for (int i = 0; i < _stacks.Count; i++)
        {
            stackRecs[i] = new StackRecord { FirstFrame = (uint)pool.Count, FrameCount = (uint)_stacks[i].Length };
            pool.AddRange(_stacks[i]);
        }

        w.AddSection(SectionType.Strings, StackTableFormat.Version, 0, CollectionsMarshal.AsSpan(strings), (ulong)strings.Count);
        w.AddRecords<FrameRecord>(SectionType.Frames, StackTableFormat.Version, frames);
        w.AddRecords<StackRecord>(SectionType.Stacks, StackTableFormat.Version, stackRecs);
        w.AddRecords<uint>(SectionType.StackFrames, StackTableFormat.Version, CollectionsMarshal.AsSpan(pool));
    }
}

/// <summary>Version constant for the symbol-table sections (mirrors native <c>kSymbolsVersion</c>).</summary>
public static class StackTableFormat
{
    public const ushort Version = 1;
}

using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Sherlock.Core.Storage;

/// <summary>A frame: a slice of the Strings blob. frameId is this record's index. Mirrors the native FrameRecord.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct FrameRecord
{
    public uint StrOffset;
    public uint StrLen;
}

/// <summary>A stack: a slice of the StackFrames pool. stackId is this record's index. Mirrors the native StackRecord.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct StackRecord
{
    public uint FirstFrame;
    public uint FrameCount;
}

/// <summary>Resolves a <c>stackId</c> to its frames and method names, over the interned symbol tables in a container. Names cached lazily.</summary>
public sealed class StackTable
{
    private readonly ReadOnlyMemory<byte> _strings;
    private readonly ReadOnlyMemory<byte> _frames;      // FrameRecord[]
    private readonly ReadOnlyMemory<byte> _stacks;      // StackRecord[]
    private readonly ReadOnlyMemory<byte> _stackFrames; // uint[]
    private readonly string?[] _frameCache;

    private StackTable(
        ReadOnlyMemory<byte> strings,
        ReadOnlyMemory<byte> frames,
        ReadOnlyMemory<byte> stacks,
        ReadOnlyMemory<byte> stackFrames)
    {
        _strings = strings;
        _frames = frames;
        _stacks = stacks;
        _stackFrames = stackFrames;
        _frameCache = new string?[FrameCount];
    }

    public static StackTable Read(ContainerReader c)
    {
        ReadOnlyMemory<byte> Get(SectionType t) => c.TryGetSection(t, out Section s) ? s.Data : default;
        return new StackTable(
            Get(SectionType.Strings),
            Get(SectionType.Frames),
            Get(SectionType.Stacks),
            Get(SectionType.StackFrames));
    }

    public int FrameCount => _frames.Length / Marshal.SizeOf<FrameRecord>();
    public int StackCount => _stacks.Length / Marshal.SizeOf<StackRecord>();

    /// <summary>The method name of a frame, decoded from the Strings blob (cached).</summary>
    public string Frame(uint frameId)
    {
        if (_frameCache[frameId] is { } cached)
        {
            return cached;
        }
        FrameRecord r = MemoryMarshal.Cast<byte, FrameRecord>(_frames.Span)[(int)frameId];
        string name = Encoding.UTF8.GetString(_strings.Span.Slice((int)r.StrOffset, (int)r.StrLen));
        _frameCache[frameId] = name;
        return name;
    }

    /// <summary>The frame ids of a stack, root→leaf as interned.</summary>
    public ReadOnlySpan<uint> FrameIds(uint stackId)
    {
        StackRecord r = MemoryMarshal.Cast<byte, StackRecord>(_stacks.Span)[(int)stackId];
        return MemoryMarshal.Cast<byte, uint>(_stackFrames.Span).Slice((int)r.FirstFrame, (int)r.FrameCount);
    }

    /// <summary>Resolves a stack to its method names, root→leaf.</summary>
    public string[] FrameNames(uint stackId)
    {
        ReadOnlySpan<uint> ids = FrameIds(stackId);
        var names = new string[ids.Length];
        for (int i = 0; i < ids.Length; i++)
        {
            names[i] = Frame(ids[i]);
        }
        return names;
    }

    /// <summary>Resolves a stack to its method names joined by <paramref name="separator"/>.</summary>
    public string FormatStack(uint stackId, char separator = ';')
    {
        ReadOnlySpan<uint> ids = FrameIds(stackId);
        var sb = new StringBuilder();
        for (int i = 0; i < ids.Length; i++)
        {
            if (i != 0)
            {
                sb.Append(separator);
            }
            sb.Append(Frame(ids[i]));
        }
        return sb.ToString();
    }
}

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Sherlock.Core.Storage;

namespace Sherlock.Core.Profiling;

/// <summary>A slice of the Strings section. Mirrors the native <c>StringRef</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct StringRef
{
    public uint Offset;
    public uint Length;
}

/// <summary>A module: its version id and file name. Mirrors the native <c>ModuleRecord</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct ModuleRecord
{
    public Guid Mvid;
    public StringRef Name;
}

/// <summary>A method by identity (module and token) with its name parts. Mirrors the native <c>MethodRecord</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct MethodRecord
{
    public uint Module;
    public uint Token;
    public StringRef Namespace;
    public StringRef Type;
    public StringRef Name;
    public StringRef GenericParameters;
    public StringRef Parameters;
    public StringRef QualifiedParameters;
    public StringRef ReturnType;
    public StringRef QualifiedReturnType;
    public StringRef Problem;
}

/// <summary>An allocated type's name. Mirrors the native <c>TypeRecord</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct TypeRecord
{
    public StringRef Name;
}

/// <summary>A stack: a slice of the StackFrames pool of method indices. Mirrors the native <c>StackRecord</c>.</summary>
[StructLayout(LayoutKind.Sequential)]
public struct StackRecord
{
    public uint FirstFrame;
    public uint FrameCount;
}

public static class SymbolFormat
{
    /// <summary>The deepest stack a reader accepts. The profiler records at most 64 frames; the margin leaves room
    /// to record more without letting a crafted file make every allocation record cost a huge stack.</summary>
    public const int MaxStackDepth = 4096;
}

/// <summary>A profile's methods, allocated types, and stacks, read and validated from a container. Methods are
/// <see cref="MethodFrame"/>s with labels unique within the profile; stacks share those instances.</summary>
public sealed class SymbolTable
{
    private readonly MethodFrame[][] _stacks;

    private SymbolTable(MethodFrame[] methods, string[] types, MethodFrame[][] stacks)
    {
        Methods = methods;
        Types = types;
        _stacks = stacks;
    }

    public IReadOnlyList<MethodFrame> Methods { get; }
    public IReadOnlyList<string> Types { get; }
    public int StackCount => _stacks.Length;

    private static readonly SectionType[] Sections =
        [SectionType.Strings, SectionType.Modules, SectionType.Methods, SectionType.Types, SectionType.Stacks, SectionType.StackFrames];

    public static SymbolTable Read(SlabFile slab)
    {
        foreach (SectionType section in Sections)
        {
            if (!slab.Has(section))
            {
                throw new InvalidDataException($"The profile has no {section} section.");
            }
        }

        byte[] strings = slab.Blob(SectionType.Strings);
        ModuleRecord[] modules = Records<ModuleRecord>(slab, SectionType.Modules);
        MethodRecord[] methodRecords = Records<MethodRecord>(slab, SectionType.Methods);
        TypeRecord[] typeRecords = Records<TypeRecord>(slab, SectionType.Types);
        StackRecord[] stacks = Records<StackRecord>(slab, SectionType.Stacks);
        uint[] stackFrames = Records<uint>(slab, SectionType.StackFrames);

        var text = new Text(strings);
        string[] moduleNames = modules.Select(module => text.Read(module.Name)).ToArray();
        var methods = new MethodFrame[methodRecords.Length];
        for (int i = 0; i < methods.Length; i++)
        {
            MethodRecord m = methodRecords[i];
            if (m.Module >= modules.Length)
            {
                throw new InvalidDataException("A method refers to an unknown module.");
            }
            methods[i] = new MethodFrame(modules[m.Module].Mvid, moduleNames[m.Module], m.Token, text.Read(m.Namespace),
                text.Read(m.Type), text.Read(m.Name), text.Read(m.GenericParameters), text.Read(m.Parameters),
                text.Read(m.QualifiedParameters), text.Read(m.ReturnType), text.Read(m.QualifiedReturnType), text.Read(m.Problem));
        }
        MethodLabels.Assign(methods);
        string[] types = typeRecords.Select(type => text.Read(type.Name)).ToArray();

        foreach (uint method in stackFrames)
        {
            if (method >= methods.Length)
            {
                throw new InvalidDataException("A stack refers to an unknown method.");
            }
        }
        var frames = new MethodFrame[stacks.Length][];
        for (int s = 0; s < stacks.Length; s++)
        {
            StackRecord stack = stacks[s];
            if (stack.FrameCount > SymbolFormat.MaxStackDepth)
            {
                throw new InvalidDataException($"A stack is deeper than {SymbolFormat.MaxStackDepth:N0} frames.");
            }
            if ((ulong)stack.FirstFrame + stack.FrameCount > (ulong)stackFrames.Length)
            {
                throw new InvalidDataException("A stack runs past the stack-frame table.");
            }
            frames[s] = new MethodFrame[stack.FrameCount];
            for (int i = 0; i < frames[s].Length; i++)
            {
                frames[s][i] = methods[stackFrames[stack.FirstFrame + i]];
            }
        }
        return new SymbolTable(methods, types, frames);
    }

    /// <summary>The methods of a stack, root to leaf. Sites and objects on the same stack share the list, and its
    /// instances are shared with <see cref="Methods"/>.</summary>
    public IReadOnlyList<MethodFrame> Stack(uint stackId) => _stacks[stackId];

    private static T[] Records<T>(SlabFile slab, SectionType section) where T : unmanaged
    {
        Column<T> column = slab.GetColumn<T>(section);
        var records = new T[checked((int)column.Length)];
        column.CopyTo(0, records);
        return records;
    }

    // Decodes each distinct slice once; names repeat across methods (namespaces, types, parameter lists).
    private sealed class Text(byte[] strings)
    {
        private readonly Dictionary<(uint, uint), string> _decoded = [];

        public string Read(StringRef slice)
        {
            if (slice.Length == 0)
            {
                return "";
            }
            if ((ulong)slice.Offset + slice.Length > (ulong)strings.Length)
            {
                throw new InvalidDataException("A name runs past the string table.");
            }
            if (!_decoded.TryGetValue((slice.Offset, slice.Length), out string? value))
            {
                value = Encoding.UTF8.GetString(strings, (int)slice.Offset, (int)slice.Length);
                _decoded[(slice.Offset, slice.Length)] = value;
            }
            return value;
        }
    }
}

/// <summary>Builds the symbol sections, as the native profiler does; Sherlock writes profiles only in tests.</summary>
public sealed class SymbolWriter
{
    private readonly List<byte> _strings = [];
    private readonly Dictionary<string, StringRef> _stringRefs = new(StringComparer.Ordinal);
    private readonly List<ModuleRecord> _modules = [];
    private readonly Dictionary<(Guid, string), uint> _moduleIds = [];
    private readonly List<MethodRecord> _methods = [];
    private readonly Dictionary<(Guid, uint), uint> _methodIds = [];
    private readonly List<TypeRecord> _types = [];
    private readonly Dictionary<string, uint> _typeIds = new(StringComparer.Ordinal);
    private readonly Dictionary<string, uint> _stackIds = new(StringComparer.Ordinal);
    private readonly List<uint[]> _stacks = [];

    /// <summary>Methods with a module version id are interned by (id, token); others get a record per call.</summary>
    public uint InternMethod(MethodFrame method)
    {
        bool known = method.Mvid != Guid.Empty;
        if (known && _methodIds.TryGetValue((method.Mvid, method.Token), out uint existing))
        {
            // The same method seen through another load of its module: a name beats a problem.
            if (_methods[(int)existing].Problem.Length != 0 && method.Problem is null)
            {
                _methods[(int)existing] = Record(method);
            }
            return existing;
        }
        uint id = (uint)_methods.Count;
        _methods.Add(Record(method));
        if (known)
        {
            _methodIds[(method.Mvid, method.Token)] = id;
        }
        return id;
    }

    private MethodRecord Record(MethodFrame method) => new()
    {
        Module = InternModule(method.Mvid, method.Module),
        Token = method.Token,
        Namespace = InternString(method.Namespace),
        Type = InternString(method.Type),
        Name = InternString(method.Name),
        GenericParameters = InternString(method.GenericParameters),
        Parameters = InternString(method.Parameters),
        QualifiedParameters = InternString(method.QualifiedParameters),
        ReturnType = InternString(method.ReturnType),
        QualifiedReturnType = InternString(method.QualifiedReturnType),
        Problem = InternString(method.Problem ?? ""),
    };

    public uint InternType(string name)
    {
        if (!_typeIds.TryGetValue(name, out uint id))
        {
            id = (uint)_types.Count;
            _types.Add(new TypeRecord { Name = InternString(name) });
            _typeIds[name] = id;
        }
        return id;
    }

    /// <summary>Identical method-index sequences share one stack id.</summary>
    public uint InternStack(ReadOnlySpan<uint> methods)
    {
        string key = Convert.ToBase64String(MemoryMarshal.AsBytes(methods));
        if (!_stackIds.TryGetValue(key, out uint id))
        {
            id = (uint)_stacks.Count;
            _stackIds[key] = id;
            _stacks.Add(methods.ToArray());
        }
        return id;
    }

    public void WriteTo(ContainerWriter w)
    {
        var stacks = new StackRecord[_stacks.Count];
        var pool = new List<uint>();
        for (int i = 0; i < _stacks.Count; i++)
        {
            stacks[i] = new StackRecord { FirstFrame = (uint)pool.Count, FrameCount = (uint)_stacks[i].Length };
            pool.AddRange(_stacks[i]);
        }
        w.AddBytes(SectionType.Strings, CollectionsMarshal.AsSpan(_strings));
        w.AddRecords(SectionType.Modules, CollectionsMarshal.AsSpan(_modules));
        w.AddRecords(SectionType.Methods, CollectionsMarshal.AsSpan(_methods));
        w.AddRecords(SectionType.Types, CollectionsMarshal.AsSpan(_types));
        w.AddRecords<StackRecord>(SectionType.Stacks, stacks);
        w.AddRecords(SectionType.StackFrames, CollectionsMarshal.AsSpan(pool));
    }

    private StringRef InternString(string text)
    {
        if (text.Length == 0)
        {
            return default;
        }
        if (!_stringRefs.TryGetValue(text, out StringRef slice))
        {
            byte[] utf8 = Encoding.UTF8.GetBytes(text);
            slice = new StringRef { Offset = (uint)_strings.Count, Length = (uint)utf8.Length };
            _strings.AddRange(utf8);
            _stringRefs[text] = slice;
        }
        return slice;
    }

    private uint InternModule(Guid mvid, string name)
    {
        (Guid, string) key = (mvid, mvid == Guid.Empty ? name : "");
        if (!_moduleIds.TryGetValue(key, out uint id))
        {
            id = (uint)_modules.Count;
            _modules.Add(new ModuleRecord { Mvid = mvid, Name = InternString(name) });
            _moduleIds[key] = id;
        }
        return id;
    }
}

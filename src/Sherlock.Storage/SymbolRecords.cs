using System;
using System.Runtime.InteropServices;

namespace Sherlock.Storage;

// The symbol records of storage/symbols.hpp, shared by allocation profiles and heap graphs.

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

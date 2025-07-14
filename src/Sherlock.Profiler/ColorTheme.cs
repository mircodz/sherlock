using System;

namespace Sherlock.Profiler;

public static class ColorTheme
{
    public static ConsoleColor AddressColor { get; set; } = ConsoleColor.Cyan;
    public static ConsoleColor TypeNameColor { get; set; } = ConsoleColor.Yellow;
    public static ConsoleColor FieldNameColor { get; set; } = ConsoleColor.Green;
    public static ConsoleColor FieldValueColor { get; set; } = ConsoleColor.White;
    public static ConsoleColor SizeColor { get; set; } = ConsoleColor.Magenta;
    public static ConsoleColor TreeStructureColor { get; set; } = ConsoleColor.DarkGray;
    public static ConsoleColor ErrorColor { get; set; } = ConsoleColor.Red;
    public static ConsoleColor WarningColor { get; set; } = ConsoleColor.DarkYellow;
    public static ConsoleColor InfoColor { get; set; } = ConsoleColor.Gray;
    public static ConsoleColor SuccessColor { get; set; } = ConsoleColor.Green;
    
    public static void WriteAddress(ulong address)
    {
        WriteColored($"0x{address:X}", AddressColor);
    }
    
    public static void WriteTypeName(string typeName)
    {
        WriteColored(typeName, TypeNameColor);
    }
    
    public static void WriteFieldName(string fieldName)
    {
        WriteColored(fieldName, FieldNameColor);
    }
    
    public static void WriteFieldValue(string value)
    {
        WriteColored(value, FieldValueColor);
    }
    
    public static void WriteSize(string size)
    {
        WriteColored(size, SizeColor);
    }
    
    public static void WriteTreeStructure(string structure)
    {
        WriteColored(structure, TreeStructureColor);
    }
    
    public static void WriteError(string message)
    {
        WriteColored(message, ErrorColor);
    }
    
    public static void WriteWarning(string message)
    {
        WriteColored(message, WarningColor);
    }
    
    public static void WriteInfo(string message)
    {
        WriteColored(message, InfoColor);
    }
    
    public static void WriteSuccess(string message)
    {
        WriteColored(message, SuccessColor);
    }
    
    public static void WriteColored(string text, ConsoleColor color)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.Write(text);
        Console.ForegroundColor = originalColor;
    }
    
    public static void WriteLineColored(string text, ConsoleColor color)
    {
        var originalColor = Console.ForegroundColor;
        Console.ForegroundColor = color;
        Console.WriteLine(text);
        Console.ForegroundColor = originalColor;
    }
    
    // Helper methods for complex formatted output
    public static void WriteObjectHeader(ulong address, string typeName, string indent = "")
    {
        Console.Write($"{indent}Object at ");
        WriteAddress(address);
        Console.Write(" (");
        WriteTypeName(typeName);
        Console.WriteLine(")");
    }
    
    public static void WriteFieldLine(string fieldName, string value, string indent = "", string prefix = "├─")
    {
        WriteTreeStructure($"{indent}{prefix} ");
        WriteFieldName(fieldName);
        Console.Write(": ");
        WriteFieldValue(value);
        Console.WriteLine();
    }
    
    public static void WriteReferenceLine(string fieldName, ulong targetAddress, string targetInfo, string indent = "", string prefix = "├─")
    {
        WriteTreeStructure($"{indent}{prefix} ");
        WriteFieldName(fieldName);
        Console.Write(" → ");
        WriteAddress(targetAddress);
        Console.Write(" (");
        WriteTypeName(targetInfo);
        Console.WriteLine(")");
    }
    
    public static void WriteSizeLine(string label, string size, string indent = "", string prefix = "├─")
    {
        WriteTreeStructure($"{indent}{prefix} {label}: ");
        WriteSize(size);
        Console.WriteLine();
    }
}
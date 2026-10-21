using Sherlock.Core;
using Sherlock.Heap;

namespace Sherlock.CLI.Rendering;

/// <summary>Type names from the dump as they are shown: shortened, then made printable (see <see cref="Printable"/>),
/// because the process under analysis chooses them. Pass raw names here exactly once; keep raw names for matching
/// and navigation. Markup output escapes markup on top (see <see cref="Styled"/>).</summary>
public static class DisplayNames
{
    public static string Type(string typeName) => Printable.Escape(TypeNames.Short(typeName));

    public static string Namespace(string typeName) => Printable.Escape(TypeNames.Namespace(typeName));
}

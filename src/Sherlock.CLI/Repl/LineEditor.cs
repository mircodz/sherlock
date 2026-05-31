using System.Text;

namespace Sherlock.CLI.Repl;

/// <summary>
/// A minimal interactive line reader: inline editing plus Up/Down history recall.
/// Falls back to <see cref="Console.ReadLine"/> when input is redirected (pipes,
/// <c>--exec</c>) or if raw-mode key reading is unavailable.
/// </summary>
public static class LineEditor
{
    private const string Esc = "";

    /// <param name="prompt">Plain-text prompt (no markup; its width drives cursor math).</param>
    /// <returns>The entered line, or null at end-of-input.</returns>
    public static string? ReadLine(string prompt, ReplHistory history)
    {
        if (Console.IsInputRedirected)
        {
            Console.Write(prompt);
            return Console.ReadLine();
        }

        try
        {
            return ReadLineRaw(prompt, history);
        }
        catch (Exception ex) when (ex is InvalidOperationException or IOException)
        {
            // No real console (e.g. some CI shells) — degrade gracefully.
            Console.Write(prompt);
            return Console.ReadLine();
        }
    }

    private static string ReadLineRaw(string prompt, ReplHistory history)
    {
        var buffer = new StringBuilder();
        int pos = 0;
        int historyIndex = history.Entries.Count; // one past the newest entry
        string stash = string.Empty;              // the in-progress line while browsing history

        Render(prompt, buffer, pos);

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);

            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.WriteLine();
                    return buffer.ToString();

                case ConsoleKey.Backspace:
                    if (pos > 0)
                    {
                        buffer.Remove(pos - 1, 1);
                        pos--;
                        Render(prompt, buffer, pos);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (pos < buffer.Length)
                    {
                        buffer.Remove(pos, 1);
                        Render(prompt, buffer, pos);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (pos > 0) { pos--; Render(prompt, buffer, pos); }
                    break;

                case ConsoleKey.RightArrow:
                    if (pos < buffer.Length) { pos++; Render(prompt, buffer, pos); }
                    break;

                case ConsoleKey.Home:
                    pos = 0; Render(prompt, buffer, pos);
                    break;

                case ConsoleKey.End:
                    pos = buffer.Length; Render(prompt, buffer, pos);
                    break;

                case ConsoleKey.UpArrow:
                    if (historyIndex > 0)
                    {
                        if (historyIndex == history.Entries.Count)
                            stash = buffer.ToString();
                        historyIndex--;
                        pos = Replace(buffer, history.Entries[historyIndex]);
                        Render(prompt, buffer, pos);
                    }
                    break;

                case ConsoleKey.DownArrow:
                    if (historyIndex < history.Entries.Count)
                    {
                        historyIndex++;
                        string text = historyIndex == history.Entries.Count ? stash : history.Entries[historyIndex];
                        pos = Replace(buffer, text);
                        Render(prompt, buffer, pos);
                    }
                    break;

                default:
                    if (key.Key == ConsoleKey.C && key.Modifiers.HasFlag(ConsoleModifiers.Control))
                    {
                        Console.WriteLine("^C");
                        return string.Empty; // abandon the current line, keep the session
                    }
                    if (!char.IsControl(key.KeyChar))
                    {
                        buffer.Insert(pos, key.KeyChar);
                        pos++;
                        Render(prompt, buffer, pos);
                    }
                    break;
            }
        }
    }

    private static int Replace(StringBuilder buffer, string text)
    {
        buffer.Clear();
        buffer.Append(text);
        return buffer.Length;
    }

    /// <summary>Repaints the current line and positions the cursor (ANSI).</summary>
    private static void Render(string prompt, StringBuilder buffer, int pos)
    {
        // Clear the whole line, return to column 0, draw prompt + buffer.
        Console.Write($"{Esc}[2K\r");
        Console.Write(prompt);
        Console.Write(buffer.ToString());

        // Move the cursor to prompt + pos from column 0.
        Console.Write("\r");
        int target = prompt.Length + pos;
        if (target > 0)
            Console.Write($"{Esc}[{target}C");
    }
}

using System;
using System.IO;
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
            // No real console (e.g. some CI shells) - degrade gracefully.
            Console.Write(prompt);
            return Console.ReadLine();
        }
    }

    private static string? ReadLineRaw(string prompt, ReplHistory history)
    {
        var buffer = new StringBuilder();
        int pos = 0;
        int historyIndex = history.Entries.Count; // one past the newest entry
        string stash = string.Empty;              // the in-progress line while browsing history

        // History recall: move by `delta` entries, stashing the in-progress line first.
        void MoveHistory(int delta)
        {
            int next = historyIndex + delta;
            if (next < 0 || next > history.Entries.Count)
            {
                return;
            }

            if (historyIndex == history.Entries.Count)
            {
                stash = buffer.ToString(); // remember the live line before browsing away
            }

            historyIndex = next;
            string text = historyIndex == history.Entries.Count ? stash : history.Entries[historyIndex];
            pos = Replace(buffer, text);
            Render(prompt, buffer, pos);
        }

        Render(prompt, buffer, pos);

        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
            bool alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);

            // Readline/emacs-style bindings (bash defaults): Ctrl+A/E, Ctrl+B/F, Ctrl+P/N,
            // Ctrl+W/U/K kills, Ctrl+D EOF/delete, Ctrl+L clear, Alt+B/F word motion.
            if (ctrl && !alt)
            {
                switch (key.Key)
                {
                    case ConsoleKey.A: pos = 0; Render(prompt, buffer, pos); continue;
                    case ConsoleKey.E: pos = buffer.Length; Render(prompt, buffer, pos); continue;
                    case ConsoleKey.B: if (pos > 0) { pos--; Render(prompt, buffer, pos); } continue;
                    case ConsoleKey.F: if (pos < buffer.Length) { pos++; Render(prompt, buffer, pos); } continue;
                    case ConsoleKey.P: MoveHistory(-1); continue;
                    case ConsoleKey.N: MoveHistory(+1); continue;

                    case ConsoleKey.K: // kill to end of line
                        if (pos < buffer.Length) { buffer.Remove(pos, buffer.Length - pos); Render(prompt, buffer, pos); }
                        continue;

                    case ConsoleKey.U: // kill to start of line
                        if (pos > 0) { buffer.Remove(0, pos); pos = 0; Render(prompt, buffer, pos); }
                        continue;

                    case ConsoleKey.W: // kill previous word
                    {
                        int start = PrevWord(buffer, pos);
                        if (start < pos) { buffer.Remove(start, pos - start); pos = start; Render(prompt, buffer, pos); }
                        continue;
                    }

                    case ConsoleKey.L: // clear screen, keep the line
                        Console.Write($"{Esc}[2J{Esc}[H");
                        Render(prompt, buffer, pos);
                        continue;

                    case ConsoleKey.D: // EOF on empty line, else delete-forward
                        if (buffer.Length == 0) { Console.WriteLine(); return null; }
                        if (pos < buffer.Length) { buffer.Remove(pos, 1); Render(prompt, buffer, pos); }
                        continue;

                    case ConsoleKey.C:
                        Console.WriteLine("^C");
                        return string.Empty; // abandon the current line, keep the session

                    case ConsoleKey.H: // Ctrl+H == backspace on many terminals
                        if (pos > 0) { buffer.Remove(pos - 1, 1); pos--; Render(prompt, buffer, pos); }
                        continue;
                }
            }

            // Alt+B / Alt+F: move by word.
            if (alt && key.Key == ConsoleKey.B) { pos = PrevWord(buffer, pos); Render(prompt, buffer, pos); continue; }
            if (alt && key.Key == ConsoleKey.F) { pos = NextWord(buffer, pos); Render(prompt, buffer, pos); continue; }

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
                    MoveHistory(-1);
                    break;

                case ConsoleKey.DownArrow:
                    MoveHistory(+1);
                    break;

                default:
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

    /// <summary>Start of the word at or before <paramref name="pos"/> (skips trailing spaces, then the word).</summary>
    private static int PrevWord(StringBuilder b, int pos)
    {
        int i = pos;
        while (i > 0 && char.IsWhiteSpace(b[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(b[i - 1])) i--;
        return i;
    }

    /// <summary>End of the word at or after <paramref name="pos"/> (skips leading spaces, then the word).</summary>
    private static int NextWord(StringBuilder b, int pos)
    {
        int i = pos;
        while (i < b.Length && char.IsWhiteSpace(b[i])) i++;
        while (i < b.Length && !char.IsWhiteSpace(b[i])) i++;
        return i;
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
        {
            Console.Write($"{Esc}[{target}C");
        }
    }
}

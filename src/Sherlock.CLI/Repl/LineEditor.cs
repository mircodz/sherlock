using System;
using System.IO;
using System.Text;
using System.Threading;
using Sherlock.CLI.Rendering;
using Spectre.Console;

namespace Sherlock.CLI.Repl;

/// <summary>Line editing and history, with a line-based fallback when raw input is unavailable.</summary>
public static class LineEditor
{
    private const string Esc = "";

    /// <param name="prompt">Plain text; its length drives cursor placement.</param>
    /// <returns>The entered line, or null at end-of-input.</returns>
    public static string? ReadLine(string prompt, ReplHistory history, IAnsiConsole console, CancellationToken cancellation = default)
    {
        cancellation.ThrowIfCancellationRequested();
        if (!Console.IsInputRedirected)
        {
            try
            {
                return ReadLineRaw(prompt, history, console, cancellation);
            }
            catch (Exception ex) when (ex is InvalidOperationException or IOException)
            {
                // Hosts without raw console support use line-based input below.
            }
        }
        Console.Write(prompt);
        return Console.In.ReadLineAsync(cancellation).AsTask().GetAwaiter().GetResult();
    }

    private static string? ReadLineRaw(string prompt, ReplHistory history, IAnsiConsole console, CancellationToken cancellation)
    {
        var buffer = new StringBuilder();
        int pos = 0;
        int historyIndex = history.Entries.Count; // one past the newest entry
        string stash = string.Empty;

        // Preserve the unfinished line while browsing history.
        void MoveHistory(int delta)
        {
            int next = historyIndex + delta;
            if (next < 0 || next > history.Entries.Count)
            {
                return;
            }

            if (historyIndex == history.Entries.Count)
            {
                stash = buffer.ToString();
            }

            historyIndex = next;
            string text = historyIndex == history.Entries.Count ? stash : history.Entries[historyIndex];
            buffer.Clear();
            buffer.Append(text);
            pos = buffer.Length;
            Render(console, prompt, buffer, pos);
        }

        Render(console, prompt, buffer, pos);

        while (true)
        {
            cancellation.ThrowIfCancellationRequested();
            if (!Console.KeyAvailable)
            {
                Thread.Sleep(25);
                continue;
            }
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            bool ctrl = key.Modifiers.HasFlag(ConsoleModifiers.Control);
            bool alt = key.Modifiers.HasFlag(ConsoleModifiers.Alt);

            // Readline-style control keys.
            if (ctrl && !alt)
            {
                switch (key.Key)
                {
                    case ConsoleKey.A: pos = 0; Render(console, prompt, buffer, pos); continue;
                    case ConsoleKey.E: pos = buffer.Length; Render(console, prompt, buffer, pos); continue;
                    case ConsoleKey.B: if (pos > 0) { pos--; Render(console, prompt, buffer, pos); } continue;
                    case ConsoleKey.F: if (pos < buffer.Length) { pos++; Render(console, prompt, buffer, pos); } continue;
                    case ConsoleKey.P: MoveHistory(-1); continue;
                    case ConsoleKey.N: MoveHistory(+1); continue;

                    case ConsoleKey.K: // kill to end of line
                        if (pos < buffer.Length) { buffer.Remove(pos, buffer.Length - pos); Render(console, prompt, buffer, pos); }
                        continue;

                    case ConsoleKey.U: // kill to start of line
                        if (pos > 0) { buffer.Remove(0, pos); pos = 0; Render(console, prompt, buffer, pos); }
                        continue;

                    case ConsoleKey.W: // kill previous word
                        {
                            int start = PrevWord(buffer, pos);
                            if (start < pos) { buffer.Remove(start, pos - start); pos = start; Render(console, prompt, buffer, pos); }
                            continue;
                        }

                    case ConsoleKey.L: // clear screen, keep the line
                        Console.Write($"{Esc}[2J{Esc}[H");
                        Render(console, prompt, buffer, pos);
                        continue;

                    case ConsoleKey.D: // EOF on empty line, else delete-forward
                        if (buffer.Length == 0) { Console.WriteLine(); return null; }
                        if (pos < buffer.Length) { buffer.Remove(pos, 1); Render(console, prompt, buffer, pos); }
                        continue;

                    case ConsoleKey.C:
                        Console.WriteLine("^C");
                        throw new OperationCanceledException(); // do not repeat the previous command

                    case ConsoleKey.H: // Ctrl+H == backspace on many terminals
                        if (pos > 0) { buffer.Remove(pos - 1, 1); pos--; Render(console, prompt, buffer, pos); }
                        continue;
                }
            }

            // Alt+B / Alt+F: move by word.
            if (alt && key.Key == ConsoleKey.B) { pos = PrevWord(buffer, pos); Render(console, prompt, buffer, pos); continue; }
            if (alt && key.Key == ConsoleKey.F) { pos = NextWord(buffer, pos); Render(console, prompt, buffer, pos); continue; }

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
                        Render(console, prompt, buffer, pos);
                    }
                    break;

                case ConsoleKey.Delete:
                    if (pos < buffer.Length)
                    {
                        buffer.Remove(pos, 1);
                        Render(console, prompt, buffer, pos);
                    }
                    break;

                case ConsoleKey.LeftArrow:
                    if (pos > 0) { pos--; Render(console, prompt, buffer, pos); }
                    break;

                case ConsoleKey.RightArrow:
                    if (pos < buffer.Length) { pos++; Render(console, prompt, buffer, pos); }
                    break;

                case ConsoleKey.Home:
                    pos = 0; Render(console, prompt, buffer, pos);
                    break;

                case ConsoleKey.End:
                    pos = buffer.Length; Render(console, prompt, buffer, pos);
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
                        Render(console, prompt, buffer, pos);
                    }
                    break;
            }
        }
    }

    /// <summary>Previous word boundary, skipping whitespace.</summary>
    private static int PrevWord(StringBuilder b, int pos)
    {
        int i = pos;
        while (i > 0 && char.IsWhiteSpace(b[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(b[i - 1])) i--;
        return i;
    }

    /// <summary>Next word end, skipping leading whitespace.</summary>
    private static int NextWord(StringBuilder b, int pos)
    {
        int i = pos;
        while (i < b.Length && char.IsWhiteSpace(b[i])) i++;
        while (i < b.Length && !char.IsWhiteSpace(b[i])) i++;
        return i;
    }

    private static void Render(IAnsiConsole console, string prompt, StringBuilder buffer, int pos)
    {
        Console.Write($"{Esc}[2K\r");
        console.Markup($"[{Theme.Focus}]{Markup.Escape(prompt)}[/]");
        Console.Write(buffer.ToString());

        Console.Write("\r");
        int target = prompt.Length + pos;
        if (target > 0)
        {
            Console.Write($"{Esc}[{target}C");
        }
    }
}

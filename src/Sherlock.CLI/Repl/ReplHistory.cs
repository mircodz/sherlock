using System;
using System.Collections.Generic;
using System.IO;

namespace Sherlock.CLI.Repl;

/// <summary>
/// Command history for the interactive REPL, persisted across sessions to a
/// file in the user's home directory.
/// </summary>
public sealed class ReplHistory
{
    private readonly string? _path;
    private readonly List<string> _entries = [];

    public ReplHistory(string? path)
    {
        _path = path;
        Load();
    }

    public IReadOnlyList<string> Entries => _entries;

    public string? Last => _entries.Count > 0 ? _entries[^1] : null;

    /// <summary>Records a line, skipping blanks and consecutive duplicates.</summary>
    public void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        if (_entries.Count > 0 && _entries[^1] == line)
        {
            return;
        }

        _entries.Add(line);
        Append(line);
    }

    public static string DefaultPath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".sherlock_history");

    private void Load()
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            if (File.Exists(_path))
            {
                _entries.AddRange(File.ReadAllLines(_path));
            }
        }
        catch
        {
            // History is best-effort; a missing or unreadable file is not fatal.
        }
    }

    private void Append(string line)
    {
        if (_path is null)
        {
            return;
        }

        try
        {
            File.AppendAllText(_path, line + Environment.NewLine);
        }
        catch
        {
            // Ignore write failures (read-only home, etc.).
        }
    }
}

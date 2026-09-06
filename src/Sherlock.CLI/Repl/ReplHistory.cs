using System;
using System.Collections.Generic;
using System.IO;

namespace Sherlock.CLI.Repl;

/// <summary>REPL history with best-effort persistence.</summary>
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
        if (string.IsNullOrWhiteSpace(line) || (_entries.Count > 0 && _entries[^1] == line))
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
            // History failures must not prevent analysis.
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
            // History failures must not prevent analysis.
        }
    }
}

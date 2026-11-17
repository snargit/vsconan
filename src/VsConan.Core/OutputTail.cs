using System;
using System.Collections.Generic;
using System.Linq;

namespace VsConan.Core;

/// <summary>
/// The last lines of a process's output, kept as it streams, so a failure can be reported with
/// Conan's own error message without holding the whole log.
/// </summary>
internal sealed class OutputTail
{
    private readonly int _maxLines;
    private readonly int _maxCharacters;
    private readonly Queue<string> _lines = new();

    public OutputTail(int maxLines = 20, int maxCharacters = 4000)
    {
        _maxLines = maxLines;
        _maxCharacters = maxCharacters;
    }

    /// <summary>Adds a line; blank lines are skipped. Safe to call from the output threads.</summary>
    public void Add(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        lock (_lines)
        {
            _lines.Enqueue(line.TrimEnd());
            if (_lines.Count > _maxLines)
            {
                _lines.Dequeue();
            }
        }
    }

    /// <summary>The kept lines, newest last, cut from the start to the character limit; null if there are none.</summary>
    public override string? ToString()
    {
        string text;
        lock (_lines)
        {
            if (_lines.Count == 0)
            {
                return null;
            }

            text = string.Join("\n", _lines.ToArray());
        }

        if (text.Length <= _maxCharacters)
        {
            return text;
        }

        // Keep the end, where Conan prints the error, starting at a line boundary if there is one.
        var cut = text.Substring(text.Length - _maxCharacters);
        var newline = cut.IndexOf('\n');
        return newline >= 0 && newline < cut.Length - 1 ? cut.Substring(newline + 1) : cut;
    }
}

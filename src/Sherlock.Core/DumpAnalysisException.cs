namespace Sherlock.Core;

/// <summary>
/// Raised for expected, user-facing analysis failures (e.g. a dump with no
/// managed runtime). The CLI renders the message rather than a stack trace.
/// </summary>
public sealed class DumpAnalysisException : Exception
{
    public DumpAnalysisException(string message) : base(message) { }
    public DumpAnalysisException(string message, Exception inner) : base(message, inner) { }
}

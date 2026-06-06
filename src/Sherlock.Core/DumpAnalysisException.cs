using System;

namespace Sherlock.Core;

public sealed class DumpAnalysisException : Exception
{
    public DumpAnalysisException(string message) : base(message) { }
    public DumpAnalysisException(string message, Exception inner) : base(message, inner) { }
}

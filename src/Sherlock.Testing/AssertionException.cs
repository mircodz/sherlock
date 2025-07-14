namespace Sherlock.Testing;

/// <summary>
/// Exception thrown when a heap assertion fails.
/// </summary>
public class AssertionException : Exception
{
    public AssertionException(string message) : base(message) { }
}
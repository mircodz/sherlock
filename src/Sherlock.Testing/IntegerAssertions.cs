namespace Sherlock.Testing;

/// <summary>
/// Extension methods for integer assertions in tests.
/// </summary>
public static class IntegerAssertions
{
    /// <summary>
    /// Creates an assertion context for an integer value.
    /// </summary>
    public static IntegerAssertion Should(this int actualValue)
    {
        return new IntegerAssertion(actualValue);
    }
}

/// <summary>
/// Provides assertion methods for integer values.
/// </summary>
public class IntegerAssertion
{
    private readonly int _actualValue;

    internal IntegerAssertion(int actualValue)
    {
        _actualValue = actualValue;
    }

    /// <summary>
    /// Asserts that the value is greater than the expected minimum.
    /// </summary>
    public void BeGreaterThan(int expectedMinimum)
    {
        if (_actualValue <= expectedMinimum)
        {
            throw new AssertionException($"Expected value to be greater than {expectedMinimum}, but was {_actualValue}");
        }
    }

    /// <summary>
    /// Asserts that the value is greater than or equal to the expected minimum.
    /// </summary>
    public void BeGreaterOrEqualTo(int expectedMinimum)
    {
        if (_actualValue < expectedMinimum)
        {
            throw new AssertionException($"Expected value to be greater than or equal to {expectedMinimum}, but was {_actualValue}");
        }
    }

    /// <summary>
    /// Asserts that the value is less than the expected maximum.
    /// </summary>
    public void BeLessThan(int expectedMaximum)
    {
        if (_actualValue >= expectedMaximum)
        {
            throw new AssertionException($"Expected value to be less than {expectedMaximum}, but was {_actualValue}");
        }
    }

    /// <summary>
    /// Asserts that the value equals the expected value.
    /// </summary>
    public void Be(int expectedValue)
    {
        if (_actualValue != expectedValue)
        {
            throw new AssertionException($"Expected value to be {expectedValue}, but was {_actualValue}");
        }
    }
}
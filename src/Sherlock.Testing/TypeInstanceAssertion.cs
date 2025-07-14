namespace Sherlock.Testing;

/// <summary>
/// Provides assertion methods for type instance counts in unit tests.
/// </summary>
public class TypeInstanceAssertion
{
    private readonly int _actualCount;
    private readonly string _typeName;

    internal TypeInstanceAssertion(int actualCount, string typeName)
    {
        _actualCount = actualCount;
        _typeName = typeName;
    }

    /// <summary>
    /// Asserts that the instance count equals the expected value.
    /// </summary>
    public void Be(int expectedCount)
    {
        if (_actualCount != expectedCount)
        {
            throw new AssertionException($"Expected {_typeName} to have {expectedCount} instances, but found {_actualCount}");
        }
    }

    /// <summary>
    /// Asserts that there are no instances of this type.
    /// </summary>
    public void BeEmpty()
    {
        Be(0);
    }

    /// <summary>
    /// Asserts that the instance count is greater than the expected value.
    /// </summary>
    public void BeGreaterThan(int expectedMinimum)
    {
        if (_actualCount <= expectedMinimum)
        {
            throw new AssertionException($"Expected {_typeName} to have more than {expectedMinimum} instances, but found {_actualCount}");
        }
    }

    /// <summary>
    /// Asserts that the instance count is greater than or equal to the expected value.
    /// </summary>
    public void BeGreaterOrEqualTo(int expectedMinimum)
    {
        if (_actualCount < expectedMinimum)
        {
            throw new AssertionException($"Expected {_typeName} to have at least {expectedMinimum} instances, but found {_actualCount}");
        }
    }
}
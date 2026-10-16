using System;
using System.Globalization;
using System.IO;
using Sherlock.Core.Analysis;

namespace Sherlock.Core.Tests.Analysis;

public sealed class ObjectInspectionTests
{
    [Fact]
    public void MissingDumpMemoryIsAnUnreadableValue()
    {
        Assert.True(ObjectInspector.IsReadError(new InvalidDataException("Cannot read memory at 0x11.")));
        Assert.True(ObjectInspector.IsReadError(new IOException("Dump read failed.")));
    }

    [Fact]
    public void LifetimeCancellationAndResourceFailuresAreNotUnreadableValues()
    {
        Assert.False(ObjectInspector.IsReadError(new ObjectDisposedException("snapshot")));
        Assert.False(ObjectInspector.IsReadError(new OperationCanceledException()));
        Assert.False(ObjectInspector.IsReadError(new OutOfMemoryException()));
    }

    [Theory]
    [InlineData("<Payload>k__BackingField", "Payload")]
    [InlineData("_firstChar", "_firstChar")]
    [InlineData("<>k__BackingField", "<>k__BackingField")]
    [InlineData(null, "<field>")]
    public void BackingFieldsDisplayAsTheirProperty(string? field, string expected)
    {
        Assert.Equal(expected, ObjectInspector.FieldDisplayName(field));
    }

    [Theory]
    [InlineData(-1, 64, "startIndex")]
    [InlineData(0, 0, "count")]
    [InlineData(0, -1, "count")]
    [InlineData(0, 1025, "count")]
    [InlineData(0, int.MaxValue, "count")]
    public void InvalidPagesFailBeforeReadingTheDump(int startIndex, int count, string parameter)
    {
        var inspector = new ObjectInspector(null!);
        var value = new ObjectValue("$", "Object", "0x1234", ObjectValueKind.Reference, 0x1234);
        Assert.Equal(parameter, Assert.Throws<ArgumentOutOfRangeException>(() => inspector.InspectChildren(value, startIndex, count)).ParamName);
    }

    [Theory]
    [InlineData(ObjectValueKind.Number)]
    [InlineData(ObjectValueKind.Boolean)]
    [InlineData(ObjectValueKind.Character)]
    [InlineData(ObjectValueKind.String)]
    [InlineData(ObjectValueKind.Null)]
    [InlineData(ObjectValueKind.Pointer)]
    [InlineData(ObjectValueKind.Unreadable)]
    [InlineData(ObjectValueKind.Other)]
    public void LeafInspectionNeverDereferencesAnAddress(ObjectValueKind kind)
    {
        var inspector = new ObjectInspector(null!);
        var value = new ObjectValue("field", "type", "preview", kind, 0x1234);

        Assert.False(value.CanExpand);
        InspectionPage page = inspector.InspectChildren(value, int.MaxValue, 1024);
        Assert.Empty(page.Items);
        Assert.Equal(0, page.TotalCount);
        Assert.Equal(int.MaxValue, page.NextIndex);
        Assert.False(page.HasMore);
        Assert.False(page.HasRawFields);
    }

    [Fact]
    public void PageTracksContinuationSeparatelyFromCollectionCapacity()
    {
        ObjectValue[] items = [new("[64]", "System.Int32", "123", ObjectValueKind.Number)];
        var page = new InspectionPage(items, 66, 64, CollectionCount: 66, Capacity: 128) { HasRawFields = true };

        Assert.Equal(65, page.NextIndex);
        Assert.True(page.HasMore);
        Assert.Equal(66, page.CollectionCount);
        Assert.Equal(128, page.Capacity);
        Assert.True(page.HasRawFields);
        Assert.False((page with { TotalCount = 65 }).HasMore);
    }
}

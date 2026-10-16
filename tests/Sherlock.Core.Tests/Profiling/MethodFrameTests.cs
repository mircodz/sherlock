using System;
using System.Collections.Generic;
using System.Linq;
using Sherlock.Core.Profiling;
using Xunit;

namespace Sherlock.Core.Tests.Profiling;

public sealed class MethodFrameTests
{
    private static readonly Guid AppMvid = Guid.NewGuid();

    private static MethodFrame Frame(string ns, string type, string name, string parameters = "()", string? qualifiedParameters = null,
        string returnType = "void", string? qualifiedReturnType = null, string module = "App.dll", Guid? mvid = null, uint token = 0x06000001,
        string genericParameters = "", string? problem = null) =>
        new(mvid ?? AppMvid, module, token, ns, type, name, genericParameters, parameters, qualifiedParameters ?? parameters,
            returnType, qualifiedReturnType ?? returnType, problem);

    private static string[] Labels(params MethodFrame[] methods)
    {
        MethodLabels.Assign(methods);
        return methods.Select(method => method.Label).ToArray();
    }

    [Fact]
    public void LabelsAreTypeMethodAndParameters()
    {
        Assert.Equal(
            ["List<T>.Add(T)", "Surface..ctor(int, int)", "Enumerable.Select<TSource, TResult>(IEnumerable<TSource>, Func<TSource, TResult>)", "Outer+Inner.Run()"],
            Labels(
                Frame("System.Collections.Generic", "List<T>", "Add", "(T)", token: 1),
                Frame("App", "Surface", ".ctor", "(int, int)", token: 2),
                Frame("System.Linq", "Enumerable", "Select", "(IEnumerable<TSource>, Func<TSource, TResult>)", genericParameters: "<TSource, TResult>", token: 3),
                Frame("", "Outer+Inner", "Run", token: 4)));
    }

    [Fact]
    public void ANamespaceTellsSameNamedTypesApart()
    {
        Assert.Equal(["App.Api.Mapper.Map(Order)", "App.Data.Mapper.Map(Order)", "Program.Main()"], Labels(
            Frame("App.Api", "Mapper", "Map", "(Order)", token: 1),
            Frame("App.Data", "Mapper", "Map", "(Order)", token: 2),
            Frame("App", "Program", "Main", token: 3)));
    }

    [Fact]
    public void QualifiedParametersTellOverloadsOfSameNamedTypesApart()
    {
        Assert.Equal(["Mapper.Map(Api.Order)", "Mapper.Map(Data.Order)"], Labels(
            Frame("App", "Mapper", "Map", "(Order)", "(Api.Order)", token: 1),
            Frame("App", "Mapper", "Map", "(Order)", "(Data.Order)", token: 2)));
    }

    [Fact]
    public void ReturnTypesTellConversionOperatorsApart()
    {
        Assert.Equal(["Money.op_Explicit(Int128):int", "Money.op_Explicit(Int128):long"], Labels(
            Frame("App", "Money", "op_Explicit", "(Int128)", "(System.Int128)", "int", token: 1),
            Frame("App", "Money", "op_Explicit", "(Int128)", "(System.Int128)", "long", token: 2)));
    }

    [Fact]
    public void ModulesTellSameNamedMethodsOfDifferentAssembliesApart()
    {
        Assert.Equal(["Mapper.Map(int) [LibA]", "Mapper.Map(int) [LibB]", "Program.Main()"], Labels(
            Frame("App", "Mapper", "Map", "(int)", module: "LibA.dll", mvid: Guid.NewGuid()),
            Frame("App", "Mapper", "Map", "(int)", module: "LibB.dll", mvid: Guid.NewGuid()),
            Frame("App", "Program", "Main", token: 2)));
    }

    [Fact]
    public void ReturnTypeNamespacesTellConversionsToSameNamedTypesApart()
    {
        Assert.Equal(["Money.op_Implicit(Money):Api.Order", "Money.op_Implicit(Money):Data.Order"], Labels(
            Frame("App", "Money", "op_Implicit", "(Money)", "(App.Money)", "Order", "Api.Order", token: 1),
            Frame("App", "Money", "op_Implicit", "(Money)", "(App.Money)", "Order", "Data.Order", token: 2)));
    }

    [Fact]
    public void ModulesAreNamedWithoutTheirFileExtensionOnly()
    {
        Assert.Equal(["Mapper.Map() [Lib.Core]", "Mapper.Map() [xunit.v3.runner.common]", "Mapper.Map() [Tool]"], Labels(
            Frame("App", "Mapper", "Map", module: "Lib.Core.dll", mvid: Guid.NewGuid()),
            Frame("App", "Mapper", "Map", module: "xunit.v3.runner.common", mvid: Guid.NewGuid()),
            Frame("App", "Mapper", "Map", module: "Tool.EXE", mvid: Guid.NewGuid())));
    }

    [Fact]
    public void OnlyTheDetailsThatTellMethodsApartAreAdded()
    {
        Assert.Equal(["X.Mapper.Map()", "Y.Mapper.Map() [LibB]", "Y.Mapper.Map() [LibC]"], Labels(
            Frame("X", "Mapper", "Map", module: "LibA.dll", mvid: Guid.NewGuid()),
            Frame("Y", "Mapper", "Map", module: "LibB.dll", mvid: Guid.NewGuid()),
            Frame("Y", "Mapper", "Map", module: "LibC.dll", mvid: Guid.NewGuid())));
    }

    [Fact]
    public void MethodsThatOnlyIdentitiesTellApartAreNumberedSkippingTakenLabels()
    {
        Assert.Equal(["Mapper.Map(int)", "Mapper.Map(int) #3", "Mapper.Map(int) #2"], Labels(
            Frame("App", "Mapper", "Map", "(int)", module: "Lib.dll", mvid: Guid.NewGuid()),
            Frame("App", "Mapper", "Map", "(int)", module: "Lib.dll", mvid: Guid.NewGuid()),
            Frame("App", "Mapper", "Map", "(int) #2", token: 9)));
    }

    [Fact]
    public void NamesCraftedSoEveryDetailCreatesANewCollisionStillGetUniqueLabels()
    {
        // Each method's qualified parameters equal the next one's short parameters, so every split by detail
        // collides again; without a bound this takes a round per method.
        var methods = new List<MethodFrame>();
        for (int i = 0; i < 2_000; i++)
        {
            methods.Add(Frame("", "T", "M", $"(a{i})", $"(a{i + 1})", token: (uint)i + 1));
        }
        methods.Add(Frame("", "T", "M", "(a0)", "(z)", token: 9_999));

        string[] labels = Labels([.. methods]);

        Assert.Equal(labels.Length, labels.Distinct().Count());
    }

    [Fact]
    public void UnresolvedMethodsSayWhatIsMissing()
    {
        MethodFrame unresolved = Frame("", "", "", "", problem: "resolver/method-props failed with HRESULT 0x80131130");

        Assert.Equal("<unresolved method>", unresolved.Label);
        Assert.Equal("resolver/method-props failed with HRESULT 0x80131130", unresolved.Problem);
        Assert.Null(Frame("App", "Program", "Main").Problem);
    }

    [Fact]
    public void LabelsEscapeTextThatCouldDriveTheTerminal()
    {
        Assert.Equal("Evil\\u001b[2J.Run\\nLine()", Frame("App", "Evil\u001b[2J", "Run\nLine").Label);
        Assert.Equal("Path.Combine\\\\u0041()", Frame("App", "Path", "Combine\\u0041").Label);
        Assert.Equal("Bidi\\u202eTxt.Run()", Frame("App", "Bidi\u202eTxt", "Run").Label);
    }

    [Fact]
    public void IdentityIsTheModuleVersionAndTheToken()
    {
        MethodFrame method = Frame("App", "Mapper", "Map", token: 7);

        Assert.Equal(method, Frame("App", "Renamed", "Other", token: 7));
        Assert.Equal(method.GetHashCode(), Frame("App", "Renamed", "Other", token: 7).GetHashCode());
        Assert.NotEqual(method, Frame("App", "Mapper", "Map", token: 8));
        Assert.NotEqual(method, Frame("App", "Mapper", "Map", token: 7, mvid: Guid.NewGuid()));
    }

    [Fact]
    public void MethodsWithoutAModuleIdentityEqualOnlyThemselves()
    {
        MethodFrame method = Frame("", "", "", mvid: Guid.Empty, problem: "module unreadable");

        Assert.Equal(method, method);
        Assert.NotEqual(method, Frame("", "", "", mvid: Guid.Empty, problem: "module unreadable"));
    }

    [Theory]
    [InlineData("Mapper.Map", true)]
    [InlineData("App.Mapper.Map", true)]
    [InlineData("Mapper.Map<T>", true)]
    [InlineData("Mapper.Map<T>(int, List<Order>)", true)]
    [InlineData("App.Mapper.Map<T>(int, System.Collections.Generic.List<App.Order>)", true)]
    [InlineData("Mapper.Map(int, List<Order>)", true)]
    [InlineData("Mapper.Map(int, List<Order>):Order", true)]
    [InlineData("App.Mapper.Map(int, System.Collections.Generic.List<App.Order>):App.Order", true)]
    [InlineData("Map", false)]
    [InlineData("Mapper.Ma", false)]
    [InlineData("mapper.map", false)]
    [InlineData("Mapper.Map(int)", false)]
    [InlineData("Other.Mapper.Map", false)]
    [InlineData("", false)]
    public void QueriesNameAMethodByTypeAndName(string query, bool matches)
    {
        MethodFrame method = Frame("App", "Mapper", "Map", "(int, List<Order>)", "(int, System.Collections.Generic.List<App.Order>)",
            "Order", "App.Order", genericParameters: "<T>");

        Assert.Equal(matches, method.Matches(query));
    }

    [Fact]
    public void QueriesNameAMethodByItsLabel()
    {
        MethodFrame first = Frame("App", "Mapper", "Map", "(int)", module: "Lib.dll", mvid: Guid.NewGuid());
        MethodFrame second = Frame("App", "Mapper", "Map", "(int)", module: "Lib.dll", mvid: Guid.NewGuid());
        MethodLabels.Assign([first, second]);

        Assert.True(second.Matches("Mapper.Map(int) #2"));
        Assert.False(first.Matches("Mapper.Map(int) #2"));
        Assert.True(first.Matches("Mapper.Map") && second.Matches("Mapper.Map"));
    }

    [Fact]
    public void UnresolvedMethodsMatchOnlyTheirLabel()
    {
        MethodFrame unresolved = Frame("", "", "", "", problem: "unknown");

        Assert.True(unresolved.Matches("<unresolved method>"));
        Assert.False(unresolved.Matches("."));
    }
}

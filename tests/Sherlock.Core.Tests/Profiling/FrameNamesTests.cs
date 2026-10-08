using Sherlock.Core.Profiling;
using Xunit;

namespace Sherlock.Core.Tests.Profiling;

public sealed class FrameNamesTests
{
    [Theory]
    [InlineData("App.Mapper.Map(Order)", "App.Mapper", "Map(Order)")]
    [InlineData("App.Mapper.Map(Api.Order)", "App.Mapper", "Map(Api.Order)")]
    [InlineData("App.Money.op_Explicit(System.Int128):long", "App.Money", "op_Explicit(System.Int128):long")]
    [InlineData("App.Cache<T>.Add<TValue>(Dictionary<string, Api.Order>)", "App.Cache<T>", "Add<TValue>(Dictionary<string, Api.Order>)")]
    [InlineData("App.Order..ctor(int)", "App.Order", ".ctor(int)")]
    [InlineData("App.Program+<>c.<Main>b__0_0()", "App.Program+<>c", "<Main>b__0_0()")]
    [InlineData("App.Mapper.Map(int) [0600001a]", "App.Mapper", "Map(int) [0600001a]")]
    [InlineData("<unresolved method frame=1 [module=0x1 token=0x06000001 lifetime=1] stage=x HRESULT=0x80004005>", "",
        "<unresolved method frame=1 [module=0x1 token=0x06000001 lifetime=1] stage=x HRESULT=0x80004005>")]
    public void SplitIgnoresDotsInsideParametersAndGenerics(string frame, string type, string method)
    {
        Assert.Equal((type, method), FrameNames.Split(frame));
    }

    [Theory]
    [InlineData("App.Data.Mapper.Map(Api.Order)", "Mapper.Map(Api.Order)")]
    [InlineData("App.Order..ctor(int)", "Order..ctor(int)")]
    [InlineData("App.Outer+Inner.Run()", "Outer+Inner.Run()")]
    [InlineData("Program.Main(string[])", "Program.Main(string[])")]
    public void ShortMethodDropsTheNamespace(string frame, string expected)
    {
        Assert.Equal(expected, FrameNames.ShortMethod(frame));
    }

    [Theory]
    [InlineData("App.Mapper.Map(Api.Order)", "App.Mapper.Map", true)]
    [InlineData("App.Mapper.Map<T>(T)", "App.Mapper.Map", true)]
    [InlineData("App.Mapper.Map(Api.Order)", "App.Mapper.Map(Api.Order)", true)]
    [InlineData("App.Money.op_Explicit(Int128):long", "App.Money.op_Explicit(Int128)", true)]
    [InlineData("App.Mapper.Map(int) [0600001a]", "App.Mapper.Map(int)", true)]
    [InlineData("App.Mapper.MapAll(int)", "App.Mapper.Map", false)]
    [InlineData("App.Mapper.Map(Api.Order)", "App.Mapper.Map(Api", false)]
    [InlineData("App.List<T>.Add(T)", "App.List", false)]
    [InlineData("App.Mapper.Map(int)", "", false)]
    public void MatchesOverloadsOnlyWhereTheQueryStops(string frame, string method, bool expected)
    {
        Assert.Equal(expected, FrameNames.Matches(frame, method));
    }
}

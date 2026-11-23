using System.Text.Json;
using Hoshin.Workflow.Common;
using Xunit;

namespace Hoshin.Tests.Unit;

public sealed class JsoncBlankTests
{
    [Theory]
    [InlineData("")]
    [InlineData("  \n")]
    [InlineData("// c")]
    [InlineData("// line 1\n// line 2\n")]
    [InlineData("/* x */")]
    [InlineData("  /* first */ \n // second \r\n ")]
    public void TryParse_EmptyOrCommentOnlyText_ReturnsTrueWithNullValue(string text)
    {
        var ok = Jsonc.TryParse(text, out var value, out var error);
        Assert.True(ok, $"Expected blank JSONC to succeed, got error: {error}");
        Assert.Null(value);
        Assert.Equal("", error);
    }

    [Fact]
    public void TryParse_UnclosedBlockComment_ReturnsFalse()
    {
        var ok = Jsonc.TryParse("/* unclosed", out var value, out var error);
        Assert.False(ok);
        Assert.Null(value);
        Assert.NotEmpty(error);
    }

    [Fact]
    public void TryParse_ValidObjectWithComments_ReturnsObject()
    {
        var ok = Jsonc.TryParse("// lead\n{ \"a\": 1, }\n/* tail */", out var value, out var error);
        Assert.True(ok, error);
        Assert.NotNull(value);
        Assert.Equal(JsonValueKind.Object, value!.Value.ValueKind);
        Assert.Equal(1, value.Value.GetProperty("a").GetInt32());
    }
}

using OxQL.Mongo.Builders;
using FluentAssertions;
using Xunit;

namespace OxQL.Tests.Mongo;

public class MongoConversionExpressionBuilderTests
{
    [Fact]
    public void CoerceToUuid_EmitsFunctionExpression()
    {
        var expr = MongoConversionExpressionBuilder.CoerceToUuid("$$localValue");

        expr.Contains("$function").Should().BeTrue();
        var fn = expr["$function"].AsBsonDocument;
        fn["lang"].AsString.Should().Be("js");
        fn["args"].AsBsonArray.Should().ContainSingle();
        fn["args"].AsBsonArray[0].AsString.Should().Be("$$localValue");
    }

    [Fact]
    public void CoerceToUuid_BodyUsesUuidConstructorAndIsNonThrowing()
    {
        var expr = MongoConversionExpressionBuilder.CoerceToUuid("$_id");

        var body = expr["$function"].AsBsonDocument["body"].AsString;
        body.Should().Contain("UUID(v)");                 // parses the string into a binary UUID
        body.Should().Contain("try");                     // non-throwing
        body.Should().Contain("catch");                   // keeps original value on failure
        body.Should().Contain("typeof v !== 'string'");   // leaves non-strings unchanged
    }

    [Theory]
    [InlineData("stringToUuid", GuidConversionDirection.StringToUuid)]
    [InlineData("STRINGTOUUID", GuidConversionDirection.StringToUuid)]
    [InlineData("uuidToString", GuidConversionDirection.UuidToString)]
    [InlineData("UuidToString", GuidConversionDirection.UuidToString)]
    public void ParseDirection_ParsesKnownTokensCaseInsensitively(string token, GuidConversionDirection expected)
    {
        MongoConversionExpressionBuilder.ParseDirection(token).Should().Be(expected);
    }

    [Fact]
    public void ParseDirection_UnknownToken_Throws()
    {
        var act = () => MongoConversionExpressionBuilder.ParseDirection("notADirection");
        act.Should().Throw<InvalidOperationException>();
    }
}

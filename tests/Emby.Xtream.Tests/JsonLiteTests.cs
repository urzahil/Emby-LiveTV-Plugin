using System.Collections.Generic;
using Emby.Xtream.Xtream.Json;
using Xunit;

namespace Emby.Xtream.Tests
{
    public class JsonLiteTests
    {
        [Fact]
        public void ParsesNestedStructures()
        {
            var o = JsonLite.Parse("{\"a\":[1,2.5,\"x\",true,false,null,{\"b\":{}}],\"c\":\"d\"}");
            var list = JsonLite.AsArray(JsonLite.Get(o, "a"));
            Assert.Equal(7, list.Count);
            Assert.Equal(1L, list[0]);
            Assert.Equal(2.5, list[1]);
            Assert.Equal("x", list[2]);
            Assert.Equal(true, list[3]);
            Assert.Equal(false, list[4]);
            Assert.Null(list[5]);
            Assert.IsType<Dictionary<string, object>>(list[6]);
            Assert.Equal("d", JsonLite.GetString(o, "c"));
        }

        [Fact]
        public void HandlesStringEscapesAndUnicode()
        {
            var o = JsonLite.Parse("{\"s\":\"a\\\"b\\\\c\\n\\t\\u00e4\\u20ac\\/\"}");
            Assert.Equal("a\"b\\c\n\t\u00e4\u20ac/", JsonLite.GetString(o, "s"));
        }

        [Fact]
        public void MixedTypeAccessors()
        {
            var o = JsonLite.Parse("{\"i\":\"123\",\"n\":123,\"f\":12.9,\"t\":\"1\",\"z\":0,\"b\":true,\"s\":\"false\",\"x\":null,\"junk\":\"abc\"}");
            Assert.Equal(123, JsonLite.GetInt(o, "i"));
            Assert.Equal(123, JsonLite.GetInt(o, "n"));
            Assert.Equal(12, JsonLite.GetInt(o, "f"));
            Assert.True(JsonLite.GetBool(o, "t"));
            Assert.False(JsonLite.GetBool(o, "z"));
            Assert.True(JsonLite.GetBool(o, "b"));
            Assert.False(JsonLite.GetBool(o, "s"));
            Assert.Equal(7, JsonLite.GetInt(o, "x", 7));
            Assert.Equal(7, JsonLite.GetInt(o, "junk", 7));
            Assert.Equal(7, JsonLite.GetInt(o, "missing", 7));
            Assert.Equal("123", JsonLite.GetString(o, "n"));
        }

        [Fact]
        public void LargeIntegersStayExact()
        {
            var o = JsonLite.Parse("{\"v\":9007199254740993}");
            Assert.Equal(9007199254740993L, JsonLite.GetLong(o, "v"));
        }

        [Fact]
        public void ToleratesBomWhitespaceAndTrailingCommas()
        {
            var o = JsonLite.Parse("\uFEFF \n{\"a\":[1,2,],}\n");
            Assert.Equal(2, JsonLite.AsArray(JsonLite.Get(o, "a")).Count);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData("{")]
        [InlineData("{\"a\":}")]
        [InlineData("[1 2]")]
        [InlineData("{\"a\":1} x")]
        [InlineData("<html>not json</html>")]
        [InlineData("\"unterminated")]
        [InlineData("{\"a\":\"\\q\"}")]
        [InlineData("tru")]
        public void InvalidInputThrowsJsonLiteException(string input)
        {
            Assert.Throws<JsonLiteException>(() => JsonLite.Parse(input));
        }

        [Fact]
        public void TryParseDoesNotThrow()
        {
            object v;
            Assert.False(JsonLite.TryParse("nope", out v));
            Assert.True(JsonLite.TryParse("[]", out v));
        }

        [Fact]
        public void DeepNestingIsRejectedNotStackOverflow()
        {
            string deep = new string('[', 5000) + new string(']', 5000);
            Assert.Throws<JsonLiteException>(() => JsonLite.Parse(deep));
        }

        [Fact]
        public void AsArrayLooseHandlesObjectsAndKeyedCollections()
        {
            Assert.Empty(JsonLite.AsArrayLoose(null));
            Assert.Empty(JsonLite.AsArrayLoose("str"));
            Assert.Single(JsonLite.AsArrayLoose(JsonLite.Parse("{\"a\":1}")));
            Assert.Equal(2, JsonLite.AsArrayLoose(JsonLite.Parse("{\"1\":{\"x\":1},\"2\":{\"x\":2}}")).Count);
        }
    }
}

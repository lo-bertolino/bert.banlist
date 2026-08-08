using System;
using System.Linq;
using Bert.Banlist.VisualStudio;
using Xunit;

namespace Bert.Banlist.VisualStudio.Tests
{
    /// <summary>
    /// The VS extension writes BannedSymbols.xml straight to disk, so "don't churn the user's file"
    /// is a correctness requirement, not a nicety: these tests pin formatting preservation as hard
    /// as they pin the resulting entry.
    /// </summary>
    public class BanXmlWriterTests
    {
        [Fact]
        public void Append_PreservesCrlfCommentsAndExistingEntries()
        {
            var existing =
                "<BannedSymbols>\r\n" +
                "  <!-- keep me -->\r\n" +
                "  <Ban kind=\"Type\" symbol=\"A.B\" replacement=\"A.C\" />\r\n" +
                "</BannedSymbols>\r\n";

            var result = BanXmlWriter.Append(existing, BanKind.Method, "System.Console.WriteLine", "My.Log.Info", "structured logging");

            Assert.Equal(
                "<BannedSymbols>\r\n" +
                "  <!-- keep me -->\r\n" +
                "  <Ban kind=\"Type\" symbol=\"A.B\" replacement=\"A.C\" />\r\n" +
                "  <Ban kind=\"Method\" symbol=\"System.Console.WriteLine\" replacement=\"My.Log.Info\" reason=\"structured logging\" />\r\n" +
                "</BannedSymbols>\r\n",
                result);
        }

        [Fact]
        public void Append_KeepsLfLineEndingsWhenTheFileUsesThem()
        {
            var existing = "<BannedSymbols>\n  <Ban kind=\"Type\" symbol=\"A.B\" />\n</BannedSymbols>\n";

            var result = BanXmlWriter.Append(existing, BanKind.Type, "A.C", null, null);

            Assert.DoesNotContain("\r", result);
            Assert.Equal(
                "<BannedSymbols>\n" +
                "  <Ban kind=\"Type\" symbol=\"A.B\" />\n" +
                "  <Ban kind=\"Type\" symbol=\"A.C\" />\n" +
                "</BannedSymbols>\n",
                result);
        }

        [Fact]
        public void Append_MatchesTheIndentationOfExistingEntries()
        {
            var existing = "<BannedSymbols>\r\n    <Ban kind=\"Type\" symbol=\"A.B\" />\r\n</BannedSymbols>\r\n";

            var result = BanXmlWriter.Append(existing, BanKind.Type, "A.C", null, null);

            Assert.Contains("\r\n    <Ban kind=\"Type\" symbol=\"A.C\" />\r\n", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \r\n")]
        public void Append_CreatesAFreshDocumentForEmptyInput(string? existing)
        {
            var result = BanXmlWriter.Append(existing, BanKind.Type, "A.B", "A.C", null);

            Assert.Equal(
                "<BannedSymbols>\r\n" +
                "  <Ban kind=\"Type\" symbol=\"A.B\" replacement=\"A.C\" />\r\n" +
                "</BannedSymbols>\r\n",
                result);
        }

        [Fact]
        public void Append_ExpandsASelfClosingRoot()
        {
            var result = BanXmlWriter.Append("<BannedSymbols />\r\n", BanKind.Namespace, "System.Data.SqlClient", null, null);

            Assert.Equal(
                "<BannedSymbols>\r\n" +
                "  <Ban kind=\"Namespace\" symbol=\"System.Data.SqlClient\" />\r\n" +
                "</BannedSymbols>\r\n",
                result);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Append_OmitsBlankReplacementAndReason(string? blank)
        {
            var result = BanXmlWriter.Append(null, BanKind.Property, "System.DateTime.Now", blank, blank);

            Assert.Contains("<Ban kind=\"Property\" symbol=\"System.DateTime.Now\" />", result);
            Assert.DoesNotContain("replacement", result);
            Assert.DoesNotContain("reason", result);
        }

        [Fact]
        public void Append_EscapesXmlSpecialCharacters()
        {
            var result = BanXmlWriter.Append(null, BanKind.Type, "N.MyList<T>", null, "use \"the\" other one & move on");

            Assert.Contains("symbol=\"N.MyList&lt;T&gt;\"", result);
            Assert.Contains("reason=\"use &quot;the&quot; other one &amp; move on\"", result);

            var parsed = BanList.Parse(result).Single();
            Assert.Equal("N.MyList<T>", parsed.Symbol);
            Assert.Equal("use \"the\" other one & move on", parsed.Reason);
        }

        [Fact]
        public void Append_RoundTripsThroughTheAnalyzersParser()
        {
            var result = BanXmlWriter.Append(null, BanKind.Method, "System.Net.WebClient.#ctor", "System.Net.Http.HttpClient", "legacy");

            var entry = BanList.Parse(result).Single();
            Assert.Equal(BanKind.Method, entry.Kind);
            Assert.Equal("System.Net.WebClient.#ctor", entry.Symbol);
            Assert.Equal("System.Net.Http.HttpClient", entry.Replacement);
            Assert.Equal("legacy", entry.Reason);
        }

        [Fact]
        public void Append_ThrowsRatherThanOverwriteMalformedXml()
        {
            Assert.Throws<FormatException>(
                () => BanXmlWriter.Append("<BannedSymbols><Ban kind=", BanKind.Type, "A.B", null, null));
        }

        [Fact]
        public void Append_ThrowsOnAForeignRootElement()
        {
            Assert.Throws<FormatException>(
                () => BanXmlWriter.Append("<SomethingElse />", BanKind.Type, "A.B", null, null));
        }

        [Fact]
        public void Append_RejectsAnEmptySymbol()
        {
            Assert.Throws<ArgumentException>(() => BanXmlWriter.Append(null, BanKind.Type, "  ", null, null));
        }

        [Fact]
        public void Contains_MatchesOnKindAndSymbolTogether()
        {
            var xml = "<BannedSymbols>\r\n  <Ban kind=\"Type\" symbol=\"A.B\" />\r\n</BannedSymbols>\r\n";

            Assert.True(BanXmlWriter.Contains(xml, BanKind.Type, "A.B"));
            Assert.False(BanXmlWriter.Contains(xml, BanKind.Method, "A.B"));
            Assert.False(BanXmlWriter.Contains(xml, BanKind.Type, "A.C"));
            Assert.False(BanXmlWriter.Contains(null, BanKind.Type, "A.B"));
        }
    }
}

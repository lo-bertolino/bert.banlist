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
                "  <Ban symbol=\"A.B\" replacement=\"A.C\" />\r\n" +
                "</BannedSymbols>\r\n";

            var result = BanXmlWriter.Append(existing, "System.Console.WriteLine", "My.Log.Info", "structured logging");

            Assert.Equal(
                "<BannedSymbols>\r\n" +
                "  <!-- keep me -->\r\n" +
                "  <Ban symbol=\"A.B\" replacement=\"A.C\" />\r\n" +
                "  <Ban symbol=\"System.Console.WriteLine\" replacement=\"My.Log.Info\" reason=\"structured logging\" />\r\n" +
                "</BannedSymbols>\r\n",
                result);
        }

        [Fact]
        public void Append_KeepsLfLineEndingsWhenTheFileUsesThem()
        {
            var existing = "<BannedSymbols>\n  <Ban symbol=\"A.B\" />\n</BannedSymbols>\n";

            var result = BanXmlWriter.Append(existing, "A.C", null, null);

            Assert.DoesNotContain("\r", result);
            Assert.Equal(
                "<BannedSymbols>\n" +
                "  <Ban symbol=\"A.B\" />\n" +
                "  <Ban symbol=\"A.C\" />\n" +
                "</BannedSymbols>\n",
                result);
        }

        [Fact]
        public void Append_MatchesTheIndentationOfExistingEntries()
        {
            var existing = "<BannedSymbols>\r\n    <Ban symbol=\"A.B\" />\r\n</BannedSymbols>\r\n";

            var result = BanXmlWriter.Append(existing, "A.C", null, null);

            Assert.Contains("\r\n    <Ban symbol=\"A.C\" />\r\n", result);
        }

        [Theory]
        [InlineData(null)]
        [InlineData("")]
        [InlineData("   \r\n")]
        public void Append_CreatesAFreshDocumentForEmptyInput(string? existing)
        {
            var result = BanXmlWriter.Append(existing, "A.B", "A.C", null);

            Assert.Equal(
                "<BannedSymbols>\r\n" +
                "  <Ban symbol=\"A.B\" replacement=\"A.C\" />\r\n" +
                "</BannedSymbols>\r\n",
                result);
        }

        [Fact]
        public void Append_ExpandsASelfClosingRoot()
        {
            var result = BanXmlWriter.Append("<BannedSymbols />\r\n", "System.Data.SqlClient", null, null);

            Assert.Equal(
                "<BannedSymbols>\r\n" +
                "  <Ban symbol=\"System.Data.SqlClient\" />\r\n" +
                "</BannedSymbols>\r\n",
                result);
        }

        [Theory]
        [InlineData("")]
        [InlineData("   ")]
        [InlineData(null)]
        public void Append_OmitsBlankReplacementAndReason(string? blank)
        {
            var result = BanXmlWriter.Append(null, "System.DateTime.Now", blank, blank);

            Assert.Contains("<Ban symbol=\"System.DateTime.Now\" />", result);
            Assert.DoesNotContain("replacement", result);
            Assert.DoesNotContain("reason", result);
        }

        [Fact]
        public void Append_EscapesXmlSpecialCharacters()
        {
            var result = BanXmlWriter.Append(null, "N.MyList<T>", null, "use \"the\" other one & move on");

            Assert.Contains("symbol=\"N.MyList&lt;T&gt;\"", result);
            Assert.Contains("reason=\"use &quot;the&quot; other one &amp; move on\"", result);

            var parsed = BanList.Parse(result).Single();
            Assert.Equal("N.MyList<T>", parsed.Symbol);
            Assert.Equal("use \"the\" other one & move on", parsed.Reason);
        }

        [Fact]
        public void Append_RoundTripsThroughTheAnalyzersParser()
        {
            var result = BanXmlWriter.Append(null, "System.Net.WebClient.#ctor", "System.Net.Http.HttpClient", "legacy");

            var entry = BanList.Parse(result).Single();
            Assert.Equal("System.Net.WebClient.#ctor", entry.Symbol);
            Assert.Equal("System.Net.Http.HttpClient", entry.Replacement);
            Assert.Equal("legacy", entry.Reason);
        }

        [Fact]
        public void Append_ThrowsRatherThanOverwriteMalformedXml()
        {
            Assert.Throws<FormatException>(
                () => BanXmlWriter.Append("<BannedSymbols><Ban kind=", "A.B", null, null));
        }

        [Fact]
        public void Append_ThrowsOnAForeignRootElement()
        {
            Assert.Throws<FormatException>(
                () => BanXmlWriter.Append("<SomethingElse />", "A.B", null, null));
        }

        [Fact]
        public void Append_RejectsAnEmptySymbol()
        {
            Assert.Throws<ArgumentException>(() => BanXmlWriter.Append(null, "  ", null, null));
        }

        [Fact]
        public void Contains_MatchesOnSymbol()
        {
            var xml = "<BannedSymbols>\r\n  <Ban symbol=\"A.B\" />\r\n</BannedSymbols>\r\n";

            Assert.True(BanXmlWriter.Contains(xml, "A.B"));
            Assert.False(BanXmlWriter.Contains(xml, "A.C"));
            Assert.False(BanXmlWriter.Contains(null, "A.B"));
        }
    }
}

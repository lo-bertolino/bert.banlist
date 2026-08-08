using Bert.Banlist.VisualStudio;
using Xunit;

namespace Bert.Banlist.VisualStudio.Tests
{
    /// <summary>
    /// The resolver is lexical by necessity — the out-of-process SDK has no semantic model — so these
    /// tests pin the guessing rules that decide what the user sees pre-filled in the prompt.
    /// </summary>
    public class SymbolTextResolverTests
    {
        /// <summary>Resolves at the caret marked by <c>$</c> in <paramref name="markedSource"/>.</summary>
        private static SymbolReference? ResolveAtMarker(string markedSource)
        {
            var caret = markedSource.IndexOf('$');
            Assert.True(caret >= 0, "Test source must contain a '$' caret marker.");
            return SymbolTextResolver.Resolve(markedSource.Remove(caret, 1), caret);
        }

        [Fact]
        public void ResolvesTheWholeDottedNameFromTheMemberSegment()
        {
            var reference = ResolveAtMarker("using System;\r\nclass C { void M() { Console.Wri$teLine(\"x\"); } }");

            Assert.NotNull(reference);
            Assert.Equal("Console.WriteLine", reference!.CaretText);
            Assert.Equal("System.Console.WriteLine", reference.BestGuess);
            Assert.Equal(BanKind.Method, reference.KindGuess);
        }

        [Fact]
        public void ResolvesOnlyTheLeadingSegmentWhenTheCaretIsOnIt()
        {
            var reference = ResolveAtMarker("using System;\r\nclass C { void M() { Cons$ole.WriteLine(\"x\"); } }");

            Assert.Equal("Console", reference!.CaretText);
            Assert.Equal("System.Console", reference.BestGuess);
            Assert.Equal(BanKind.Type, reference.KindGuess);
        }

        [Fact]
        public void RecognizesAnAlreadyQualifiedName()
        {
            var reference = ResolveAtMarker("using System;\r\nclass C { void M() { System.Console.Write$Line(\"x\"); } }");

            Assert.Equal("System.Console.WriteLine", reference!.CaretText);
            Assert.Equal("System.Console.WriteLine", reference.BestGuess);
        }

        [Fact]
        public void PrefersTheShortestUsingNamespace()
        {
            // `using System;` is far likelier to be the home of an unqualified BCL name than a deeper
            // namespace that happens to be imported too.
            var reference = ResolveAtMarker(
                "using System.Text.Json;\r\nusing System.Collections.Generic;\r\nusing System;\r\nclass C { void M() { Cons$ole.WriteLine(\"x\"); } }");

            Assert.Equal("System.Console", reference!.BestGuess);
            Assert.Contains("System.Text.Json.Console", reference.Candidates);
        }

        [Fact]
        public void ExpandsAUsingAlias()
        {
            var reference = ResolveAtMarker(
                "using Json = System.Text.Json;\r\nclass C { void M() { Json.JsonSeriali$zer.Serialize(1); } }");

            Assert.Equal("Json.JsonSerializer", reference!.CaretText);
            Assert.Equal("System.Text.Json.JsonSerializer", reference.BestGuess);
        }

        [Fact]
        public void ExpandsAStaticUsing()
        {
            var reference = ResolveAtMarker("using static System.Math;\r\nclass C { void M() { var x = M$ax(1, 2); } }");

            Assert.Equal("System.Math.Max", reference!.BestGuess);
            Assert.Equal(BanKind.Method, reference.KindGuess);
        }

        [Fact]
        public void OffersTheFileNamespaceAsACandidate()
        {
            var reference = ResolveAtMarker("namespace My.App;\r\nusing System;\r\nclass C { void M() { Hel$per.Go(); } }");

            Assert.Contains("My.App.Helper", reference!.Candidates);
            Assert.Contains("Helper", reference.Candidates);
        }

        [Fact]
        public void GuessesNamespaceInsideAUsingDirective()
        {
            var reference = ResolveAtMarker("using System.Data.Sql$Client;\r\nclass C { }");

            Assert.Equal("System.Data.SqlClient", reference!.CaretText);
            Assert.Equal(BanKind.Namespace, reference.KindGuess);
        }

        [Fact]
        public void TreatsAGenericCallAsAMethod()
        {
            var reference = ResolveAtMarker("class C { void M() { Helper.Cre$ate<int, string>(1); } }");

            Assert.Equal(BanKind.Method, reference!.KindGuess);
        }

        [Fact]
        public void TreatsAGenericTypeWithoutACallAsAType()
        {
            var reference = ResolveAtMarker("class C { Li$st<int> f; }");

            Assert.Equal(BanKind.Type, reference!.KindGuess);
        }

        [Fact]
        public void ResolvesWhenTheCaretSitsJustPastTheIdentifier()
        {
            var reference = ResolveAtMarker("using System;\r\nclass C { void M() { Console$.WriteLine(\"x\"); } }");

            Assert.Equal("Console", reference!.CaretText);
        }

        [Fact]
        public void DoesNotSwallowANumericLiteralBeforeTheDot()
        {
            var reference = ResolveAtMarker("class C { void M() { var s = 1.ToStr$ing(); } }");

            Assert.Equal("ToString", reference!.CaretText);
        }

        [Theory]
        [InlineData("class C { void M() { var x =$ 1; } }")]
        [InlineData("$")]
        public void ReturnsNullWhenTheCaretIsNotOnAnIdentifier(string markedSource)
        {
            Assert.Null(ResolveAtMarker(markedSource));
        }

        [Fact]
        public void ReturnsNullForEmptyText()
        {
            Assert.Null(SymbolTextResolver.Resolve(null, 0));
            Assert.Null(SymbolTextResolver.Resolve(string.Empty, 0));
        }

        [Fact]
        public void ClampsAnOutOfRangeCaret()
        {
            Assert.Null(SymbolTextResolver.Resolve("class C { }", 9999));
        }

        [Fact]
        public void CandidatesAreDistinctAndBestGuessIsFirst()
        {
            var reference = ResolveAtMarker("using System;\r\nusing System;\r\nclass C { void M() { Cons$ole.WriteLine(\"x\"); } }");

            Assert.Equal(reference!.Candidates[0], reference.BestGuess);
            Assert.Equal(reference.Candidates.Count, System.Linq.Enumerable.Count(System.Linq.Enumerable.Distinct(reference.Candidates)));
        }
    }
}

using System.Threading.Tasks;
using Xunit;

namespace Bert.Banlist.Tests
{
    /// <summary>
    /// A namespace ban is about <em>using</em> symbols from that namespace. Code that merely lives
    /// inside it — its own locals, parameters, type parameters, labels — is not a usage and must
    /// stay clean, otherwise a team that bans its own legacy namespace drowns every file in it.
    /// </summary>
    public class NamespaceBanScopeTests
    {
        private const string BanXml = """
            <BannedSymbols>
              <Ban symbol="Legacy.Stuff" replacement="New.Stuff" />
            </BannedSymbols>
            """;

        private static BanAnalyzerTest Create(string source)
        {
            var test = new BanAnalyzerTest { TestCode = source };
            test.TestState.AdditionalFiles.Add(("BannedSymbols.xml", BanXml));
            return test;
        }

        [Fact]
        public async Task LocalsAndParametersInsideBannedNamespace_NotReported()
        {
            await Create("""
                namespace Legacy.Stuff
                {
                    public class Widget
                    {
                        public string Concat(string a, string b)
                        {
                            var joined = a + b;
                            return joined;
                        }
                    }
                }
                """).RunAsync();
        }

        [Fact]
        public async Task TypeParameterInsideBannedNamespace_NotReported()
        {
            await Create("""
                namespace Legacy.Stuff
                {
                    public class Box<T>
                    {
                        public T Value { get; set; }

                        public T Echo(T input) => input;
                    }
                }
                """).RunAsync();
        }

        [Fact]
        public async Task UsageFromOutside_StillReported()
        {
            var test = Create("""
                namespace Legacy.Stuff
                {
                    public class Widget { }
                }

                class Consumer
                {
                    object M() => new Legacy.Stuff.{|#0:Widget|}();
                }
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.Widget", "New.Stuff").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task UsageFromInsideBannedNamespace_StillReported()
        {
            // Referencing a banned-namespace *type* is a usage even from within the namespace.
            var test = Create("""
                namespace Legacy.Stuff
                {
                    public class Widget { }

                    public class Consumer
                    {
                        object M() => new {|#0:Widget|}();
                    }
                }
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.Widget", "New.Stuff").WithLocation(0));
            await test.RunAsync();
        }
    }
}

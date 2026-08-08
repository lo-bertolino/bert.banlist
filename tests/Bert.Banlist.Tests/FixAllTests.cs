using System.Threading.Tasks;
using Xunit;

namespace Bert.Banlist.Tests
{
    /// <summary>
    /// FixAll coverage. The test framework automatically verifies the batch fixer at document,
    /// project, and solution scope whenever a fixed state is provided, so multi-document and
    /// multi-project setups here exercise all three lightbulb scopes.
    /// </summary>
    public class FixAllTests
    {
        private const string MixedBanXml = """
            <BannedSymbols>
              <Ban kind="Type" symbol="Legacy.Stuff.OldHelper" replacement="New.Stuff.NewHelper" />
              <Ban kind="Method" symbol="Legacy.Stuff.OldLogger.Log(System.String)" replacement="New.Stuff.NewLogger.Log" />
            </BannedSymbols>
            """;

        [Fact]
        public async Task FixAll_MultipleDifferentBansInOneDocument()
        {
            var test = new BanCodeFixTest
            {
                TestCode = """
                    using Legacy.Stuff;

                    class C
                    {
                        void M()
                        {
                            var x = new {|#0:OldHelper|}();
                            var y = new {|#1:OldHelper|}(5);
                            OldLogger.{|#2:Log|}("a");
                        }
                    }
                    """,
                FixedCode = """
                    using New.Stuff;

                    class C
                    {
                        void M()
                        {
                            var x = new NewHelper();
                            var y = new NewHelper(5);
                            NewLogger.Log("a");
                        }
                    }
                    """,
            };
            test.TestState.Sources.Add(TestSources.Definitions);
            test.FixedState.Sources.Add(TestSources.Definitions);
            test.TestState.AdditionalFiles.Add(("BannedSymbols.xml", MixedBanXml));
            test.FixedState.AdditionalFiles.Add(("BannedSymbols.xml", MixedBanXml));
            test.ExpectedDiagnostics.Add(Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NewHelper").WithLocation(0));
            test.ExpectedDiagnostics.Add(Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NewHelper").WithLocation(1));
            test.ExpectedDiagnostics.Add(Ban.WithReplacement("Legacy.Stuff.OldLogger.Log(string)", "New.Stuff.NewLogger.Log").WithLocation(2));
            // Two distinct ban entries = two equivalence groups; the batch fixer resolves each
            // group in its own FixAll pass.
            test.NumberOfFixAllIterations = 2;
            await test.RunAsync();
        }

        [Fact]
        public async Task FixAll_AcrossDocumentsInProject()
        {
            var test = new BanCodeFixTest
            {
                TestCode = """
                    using Legacy.Stuff;

                    class C1
                    {
                        object M() => new {|#0:OldHelper|}();
                    }
                    """,
                FixedCode = """
                    using New.Stuff;

                    class C1
                    {
                        object M() => new NewHelper();
                    }
                    """,
            };
            test.TestState.Sources.Add("""
                using Legacy.Stuff;

                class C2
                {
                    void M() => OldLogger.{|#1:Log|}("b");
                }
                """);
            test.FixedState.Sources.Add("""
                using New.Stuff;

                class C2
                {
                    void M() => NewLogger.Log("b");
                }
                """);
            test.TestState.Sources.Add(TestSources.Definitions);
            test.FixedState.Sources.Add(TestSources.Definitions);
            test.TestState.AdditionalFiles.Add(("BannedSymbols.xml", MixedBanXml));
            test.FixedState.AdditionalFiles.Add(("BannedSymbols.xml", MixedBanXml));
            test.ExpectedDiagnostics.Add(Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NewHelper").WithLocation(0));
            test.ExpectedDiagnostics.Add(Ban.WithReplacement("Legacy.Stuff.OldLogger.Log(string)", "New.Stuff.NewLogger.Log").WithLocation(1));
            test.NumberOfFixAllIterations = 2;
            await test.RunAsync();
        }

        [Fact]
        public async Task FixAll_AcrossProjectsInSolution()
        {
            const string BanXml = """
                <BannedSymbols>
                  <Ban kind="Type" symbol="Legacy.Stuff.OldHelper" replacement="New.Stuff.NewHelper" />
                </BannedSymbols>
                """;
            var test = new BanCodeFixTest
            {
                TestCode = """
                    using Legacy.Stuff;

                    class C1
                    {
                        object M() => new {|#0:OldHelper|}();
                    }
                    """,
                FixedCode = """
                    using New.Stuff;

                    class C1
                    {
                        object M() => new NewHelper();
                    }
                    """,
            };
            test.TestState.Sources.Add(TestSources.Definitions);
            test.FixedState.Sources.Add(TestSources.Definitions);
            test.TestState.AdditionalFiles.Add(("BannedSymbols.xml", BanXml));
            test.FixedState.AdditionalFiles.Add(("BannedSymbols.xml", BanXml));

            // A second, independent project with its own copy of the ban list.
            test.TestState.AdditionalProjects["Second"].Sources.Add("""
                using Legacy.Stuff;

                class C2
                {
                    object M() => new {|#1:OldHelper|}();
                }
                """);
            test.TestState.AdditionalProjects["Second"].Sources.Add(TestSources.Definitions);
            test.TestState.AdditionalProjects["Second"].AdditionalFiles.Add(("BannedSymbols.xml", BanXml));
            test.FixedState.AdditionalProjects["Second"].Sources.Add("""
                using New.Stuff;

                class C2
                {
                    object M() => new NewHelper();
                }
                """);
            test.FixedState.AdditionalProjects["Second"].Sources.Add(TestSources.Definitions);
            test.FixedState.AdditionalProjects["Second"].AdditionalFiles.Add(("BannedSymbols.xml", BanXml));

            test.ExpectedDiagnostics.Add(Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NewHelper").WithLocation(0));
            test.ExpectedDiagnostics.Add(Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NewHelper").WithLocation(1));

            // Document and project scope clean up one project at a time (2 passes); solution scope
            // fixes both projects in one. Negative = "at most N iterations" in the test framework,
            // covering both since project and solution scope share this knob.
            test.NumberOfFixAllInDocumentIterations = 2;
            test.NumberOfFixAllIterations = -2;
            await test.RunAsync();
        }
    }
}

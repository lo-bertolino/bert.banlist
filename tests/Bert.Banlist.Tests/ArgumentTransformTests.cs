using System.Threading.Tasks;
using Xunit;

namespace Bert.Banlist.Tests
{
    /// <summary>
    /// The rich <c>&lt;Param&gt;</c> plan: reshape an argument's text through a template rather than
    /// just reorder it (the plain <c>argumentMap</c> attribute, covered in <see cref="CodeFixTests"/>,
    /// only reorders/selects). Covers the motivating case directly: a sync command's <c>Action</c>
    /// constructor argument becoming an async command's <c>Func&lt;Task&gt;</c> argument.
    /// </summary>
    public class ArgumentTransformTests
    {
        private static BanCodeFixTest Create(string source, string fixedSource, string banXml)
        {
            var test = new BanCodeFixTest { TestCode = source, FixedCode = fixedSource };
            test.TestState.Sources.Add(TestSources.Definitions);
            test.FixedState.Sources.Add(TestSources.Definitions);
            test.TestState.AdditionalFiles.Add(("BannedSymbols.xml", banXml));
            test.FixedState.AdditionalFiles.Add(("BannedSymbols.xml", banXml));
            return test;
        }

        [Fact]
        public async Task TypeBan_AsyncTemplate_WrapsLambdaAndWarnsCS1998()
        {
            // The exact scenario that motivated this feature: RelayCommand(Action) -> AsyncRelayCommand(Func<Task>).
            // "async () => Console.WriteLine(...)" compiles (confirmed directly against Roslyn, not
            // assumed), but with no `await` inside it the compiler also warns CS1998 — expected,
            // and mentioned in the README rather than asserted here: turning on compiler-warning
            // verification for this one case would also pull in unrelated CS1591 noise from every
            // undocumented public member in the shared test fixtures.
            var test = Create("""
                using Legacy.Stuff;
                using System;

                class C
                {
                    void M()
                    {
                        var x = new {|#0:RelayCommand|}(() => Console.WriteLine("saved"));
                    }
                }
                """, """
                using New.Stuff;
                using System;

                class C
                {
                    void M()
                    {
                        var x = new AsyncRelayCommand(async () => Console.WriteLine("saved"));
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.RelayCommand" replacement="New.Stuff.AsyncRelayCommand">
                    <Param source="0" template="async {0}" />
                  </Ban>
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.RelayCommand", "New.Stuff.AsyncRelayCommand").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task TypeBan_FactoryTemplate_WrapsValueForRetypedConstructor()
        {
            // A generic wrap, not tied to lambdas: an int-milliseconds constructor replaced by a
            // TimeSpan constructor, with the argument wrapped through a factory call.
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new {|#0:OldTimer|}(500);
                    }
                }
                """, """
                using New.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new NewTimer(System.TimeSpan.FromMilliseconds(500));
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldTimer" replacement="New.Stuff.NewTimer">
                    <Param source="0" template="System.TimeSpan.FromMilliseconds({0})" />
                  </Ban>
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldTimer", "New.Stuff.NewTimer").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task TypeBan_TemplateOnNonLambdaArgument_NoFixOffered()
        {
            // "async {0}" only makes sense applied to a lambda; substituted against a method-group
            // reference it becomes "async Save" — not a complete expression, so ParseExpression
            // reports it and the fix backs off rather than emitting nonsense. Verified empirically
            // here rather than assumed.
            var source = """
                using Legacy.Stuff;

                class C
                {
                    static void Save() { }

                    void M()
                    {
                        var x = new {|#0:RelayCommand|}(Save);
                    }
                }
                """;
            var test = Create(source, source, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.RelayCommand" replacement="New.Stuff.AsyncRelayCommand">
                    <Param source="0" template="async {0}" />
                  </Ban>
                </BannedSymbols>
                """);
            var diagnostic = Ban.WithReplacement("Legacy.Stuff.RelayCommand", "New.Stuff.AsyncRelayCommand").WithLocation(0);
            test.ExpectedDiagnostics.Add(diagnostic);
            test.FixedState.ExpectedDiagnostics.Add(diagnostic);
            await test.RunAsync();
        }

        [Fact]
        public async Task MethodBan_ParamPlan_ReordersLikeArgumentMap()
        {
            // A <Param> with no template behaves exactly like an argumentMap reorder — the general
            // mechanism subsumes the specific one.
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        OldHelper.{|#0:TrimTo|}("hello", 3);
                    }
                }
                """, """
                using New.Stuff;

                class C
                {
                    void M()
                    {
                        NewHelper.TrimTo("hello");
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper.TrimTo(System.String,System.Int32)" replacement="New.Stuff.NewHelper.TrimTo">
                    <Param source="0" />
                  </Ban>
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.TrimTo(string, int)", "New.Stuff.NewHelper.TrimTo").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task ParamPlan_TakesPrecedenceOverArgumentMap()
        {
            // Both attribute and Param present on the same entry: Param wins, argumentMap is ignored.
            // If argumentMap had won here the fix would keep both arguments unreshaped and produce
            // "new NewTimer(500)", which wouldn't compile against a TimeSpan parameter.
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new {|#0:OldTimer|}(500);
                    }
                }
                """, """
                using New.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new NewTimer(System.TimeSpan.FromMilliseconds(500));
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldTimer" replacement="New.Stuff.NewTimer" argumentMap="0">
                    <Param source="0" template="System.TimeSpan.FromMilliseconds({0})" />
                  </Ban>
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldTimer", "New.Stuff.NewTimer").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task MalformedParam_InvalidSource_FallsBackToArgumentMap()
        {
            // A <Param> with a bad "source" voids the whole plan (leniency, same as the rest of the
            // parser) — the fix falls back to argumentMap, not to no fix at all.
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        OldHelper.{|#0:Combine|}("a", "b");
                    }
                }
                """, """
                using New.Stuff;

                class C
                {
                    void M()
                    {
                        NewHelper.Combine("b", "a");
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper.Combine(System.String,System.String)" replacement="New.Stuff.NewHelper.Combine" argumentMap="1,0">
                    <Param source="not-a-number" />
                  </Ban>
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.Combine(string, string)", "New.Stuff.NewHelper.Combine").WithLocation(0));
            await test.RunAsync();
        }
    }
}

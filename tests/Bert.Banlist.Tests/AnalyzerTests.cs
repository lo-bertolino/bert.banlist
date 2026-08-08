using System.Threading.Tasks;
using Xunit;

namespace Bert.Banlist.Tests
{
    public class AnalyzerTests
    {
        private static BanAnalyzerTest Create(string source, string? banXml)
        {
            var test = new BanAnalyzerTest { TestCode = source };
            test.TestState.Sources.Add(TestSources.Definitions);
            if (banXml != null)
            {
                test.TestState.AdditionalFiles.Add(("BannedSymbols.xml", banXml));
            }

            return test;
        }

        [Fact]
        public async Task TypeBan_WithReplacementAndReason_Reported()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new {|#0:OldHelper|}();
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper" replacement="New.Stuff.NewHelper" reason="Use NewHelper — see ADR-004." />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NewHelper", "Use NewHelper — see ADR-004.").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task TypeBan_WithoutReplacement_Reported()
        {
            var test = Create("""
                class C
                {
                    void M()
                    {
                        var x = new Legacy.Stuff.{|#0:OldHelper|}();
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper" reason="Just don't." />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(Ban.Plain("Legacy.Stuff.OldHelper", "Just don't.").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task GenericTypeBan_AngleBracketForm_Reported()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    {|#0:OldList<int>|} _field;
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldList&lt;T&gt;" replacement="New.Stuff.NewList" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldList<int>", "New.Stuff.NewList").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task MethodBan_SpecificOverload_OnlyThatOverloadReported()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        OldHelper.{|#0:DoThing|}("a");
                        OldHelper.DoThing(1);
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper.DoThing(System.String)" replacement="New.Stuff.NewHelper.DoThing" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.DoThing(string)", "New.Stuff.NewHelper.DoThing").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task MethodBan_NoSignature_AllOverloadsReported()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        OldHelper.{|#0:DoThing|}("a");
                        OldHelper.{|#1:DoThing|}(1);
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper.DoThing" replacement="New.Stuff.NewHelper.DoThing" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.DoThing(string)", "New.Stuff.NewHelper.DoThing").WithLocation(0));
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.DoThing(int)", "New.Stuff.NewHelper.DoThing").WithLocation(1));
            await test.RunAsync();
        }

        [Fact]
        public async Task PropertyBan_Reported()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    string M() => OldHelper.{|#0:Value|};
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper.Value" replacement="New.Stuff.NewHelper.Value" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.Value", "New.Stuff.NewHelper.Value").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task FieldBan_Reported()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    int M() => OldHelper.{|#0:Count|};
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper.Count" replacement="New.Stuff.NewHelper.Count" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.Count", "New.Stuff.NewHelper.Count").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task ConstructorBan_SpecificOverload_ReportedOnCreation()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var a = {|#0:new OldHelper(5)|};
                        var b = new OldHelper();
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper.#ctor(System.Int32)" replacement="New.Stuff.NewHelper" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.OldHelper(int)", "New.Stuff.NewHelper").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task NamespaceBan_TypeUsageReported_MemberNotDoubleReported()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        {|#0:OldHelper|}.DoThing(1);
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff" replacement="New.Stuff" reason="Legacy namespace." />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff", "Legacy namespace.").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task UnresolvableEntry_Ignored()
        {
            // "Does.Not.Exist" resolves as neither a type nor a member, so it falls back to a
            // namespace-prefix ban that nothing in this file lives under — inert, not an error.
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new OldHelper();
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Does.Not.Exist" replacement="Nope" />
                </BannedSymbols>
                """);
            await test.RunAsync();
        }

        [Fact]
        public async Task NoKindAttribute_TypeMemberAndNamespaceAllInferredCorrectly()
        {
            // The whole point of removing `kind`: one ban list, no attribute, and a type ban, a
            // member ban, and a namespace ban all still resolve to the right thing. Each targets a
            // different symbol so there is no overlap between entries to muddy the assertions.
            var test = Create("""
                using Legacy.Stuff;
                using Legacy.Data;

                class C
                {
                    {|#0:OldList<int>|} _field;

                    void M()
                    {
                        var y = OldHelper.{|#1:Value|};
                        var z = new {|#2:Client|}();
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldList&lt;T&gt;" replacement="New.Stuff.NewList" />
                  <Ban symbol="Legacy.Stuff.OldHelper.Value" replacement="New.Stuff.NewHelper.Value" />
                  <Ban symbol="Legacy.Data" replacement="New.Data" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldList<int>", "New.Stuff.NewList").WithLocation(0));
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.Value", "New.Stuff.NewHelper.Value").WithLocation(1));
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Data.Client", "New.Data").WithLocation(2));
            await test.RunAsync();
        }

        [Fact]
        public async Task NoBanFile_NoDiagnostics()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new OldHelper();
                    }
                }
                """, banXml: null);
            await test.RunAsync();
        }

        [Fact]
        public async Task GeneratedCode_NotAnalyzed()
        {
            var test = Create("""
                // <auto-generated/>
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new OldHelper();
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper" replacement="New.Stuff.NewHelper" />
                </BannedSymbols>
                """);
            await test.RunAsync();
        }

        [Fact]
        public async Task ImplicitObjectCreation_TypeBan_Reported()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        {|#1:OldHelper|} x = {|#0:new()|};
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper" replacement="New.Stuff.NewHelper" />
                </BannedSymbols>
                """);
            // The variable's type name is one diagnostic, the implicit `new()` the other.
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NewHelper").WithLocation(1));
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NewHelper").WithLocation(0));
            await test.RunAsync();
        }
    }
}

using System.Threading.Tasks;
using Xunit;

namespace Bert.Banlist.Tests
{
    public class CodeFixTests
    {
        private const string TypeBanXml = """
            <BannedSymbols>
              <Ban symbol="Legacy.Stuff.OldHelper" replacement="New.Stuff.NewHelper" />
            </BannedSymbols>
            """;

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
        public async Task TypeSwap_UsingDirectiveSwapped()
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
                using New.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new NewHelper();
                    }
                }
                """, TypeBanXml);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NewHelper").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task TypeSwap_FullyQualified_SimplifiedWithNewUsing()
        {
            var test = Create("""
                class C
                {
                    void M()
                    {
                        Legacy.Stuff.{|#0:OldHelper|} x = null;
                    }
                }
                """, """
                using New.Stuff;

                class C
                {
                    void M()
                    {
                        NewHelper x = null;
                    }
                }
                """, TypeBanXml);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NewHelper").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task GenericTypeSwap_TypeArgumentsPreserved()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        {|#0:OldList<int>|} x = null;
                    }
                }
                """, """
                using New.Stuff;

                class C
                {
                    void M()
                    {
                        NewList<int> x = null;
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldList`1" replacement="New.Stuff.NewList" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldList<int>", "New.Stuff.NewList").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task MethodSwap_ArgumentsPreserved()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        OldHelper.{|#0:DoThing|}("a");
                    }
                }
                """, """
                using New.Stuff;

                class C
                {
                    void M()
                    {
                        NewHelper.DoThing("a");
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
        public async Task PropertySwap()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    string M() => OldHelper.{|#0:Value|};
                }
                """, """
                using New.Stuff;

                class C
                {
                    string M() => NewHelper.Value;
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
        public async Task ConstructorSwap_MatchingArity_Fixed()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new {|#0:OldHelper|}(5);
                    }
                }
                """, """
                using New.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new NewHelper(5);
                    }
                }
                """, TypeBanXml);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NewHelper").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task ConstructorSwap_NoCompatibleConstructor_NoFixOffered()
        {
            var source = """
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new {|#0:OldHelper|}(5);
                    }
                }
                """;
            // NoIntCtor has no (int) constructor: better to leave the warning than break the call.
            var test = Create(source, source, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper" replacement="New.Stuff.NoIntCtor" />
                </BannedSymbols>
                """);
            var diagnostic = Ban.WithReplacement("Legacy.Stuff.OldHelper", "New.Stuff.NoIntCtor").WithLocation(0);
            test.ExpectedDiagnostics.Add(diagnostic);
            test.FixedState.ExpectedDiagnostics.Add(diagnostic);
            await test.RunAsync();
        }

        [Fact]
        public async Task BanWithoutReplacement_NoFixOffered()
        {
            var source = """
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new {|#0:OldHelper|}();
                    }
                }
                """;
            var test = Create(source, source, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper" />
                </BannedSymbols>
                """);
            var diagnostic = Ban.Plain("Legacy.Stuff.OldHelper").WithLocation(0);
            test.ExpectedDiagnostics.Add(diagnostic);
            test.FixedState.ExpectedDiagnostics.Add(diagnostic);
            await test.RunAsync();
        }

        [Fact]
        public async Task NamespaceBan_TypeMovedToReplacementNamespace()
        {
            var test = Create("""
                using Legacy.Data;

                class C
                {
                    void M()
                    {
                        var x = new {|#0:Client|}();
                    }
                }
                """, """
                using New.Data;

                class C
                {
                    void M()
                    {
                        var x = new Client();
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Data" replacement="New.Data" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Data.Client", "New.Data").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task InstanceMethodSwap_ReceiverPreserved_ImplementsReplacementInterface()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var widget = new InstanceWidget();
                        widget.{|#0:OldInstanceMethod|}("a");
                    }
                }
                """, """
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var widget = new InstanceWidget();
                        widget.NewInstanceMethod("a");
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.InstanceWidget.OldInstanceMethod(System.String)" replacement="New.Stuff.IInstanceWidget.NewInstanceMethod" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.InstanceWidget.OldInstanceMethod(string)", "New.Stuff.IInstanceWidget.NewInstanceMethod").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task InstancePropertySwap_ReceiverPreserved_ImplementsReplacementInterface()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    string M()
                    {
                        var widget = new InstanceWidget();
                        return widget.{|#0:OldInstanceValue|};
                    }
                }
                """, """
                using Legacy.Stuff;

                class C
                {
                    string M()
                    {
                        var widget = new InstanceWidget();
                        return widget.NewInstanceValue;
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.InstanceWidget.OldInstanceValue" replacement="New.Stuff.IInstanceWidget.NewInstanceValue" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.InstanceWidget.OldInstanceValue", "New.Stuff.IInstanceWidget.NewInstanceValue").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task InstanceMethodSwap_ReceiverDerivesFromReplacementContainingType()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var widget = new InstanceWidgetDerived();
                        widget.{|#0:OldInstanceMethod2|}("a");
                    }
                }
                """, """
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var widget = new InstanceWidgetDerived();
                        widget.NewInstanceMethod2("a");
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.InstanceWidgetDerived.OldInstanceMethod2(System.String)" replacement="New.Stuff.InstanceWidgetBase.NewInstanceMethod2" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.InstanceWidgetDerived.OldInstanceMethod2(string)", "New.Stuff.InstanceWidgetBase.NewInstanceMethod2").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task InstanceMethodBan_StaticOnlyReplacement_NoFixOffered()
        {
            var source = """
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var widget = new InstanceWidget();
                        widget.{|#0:OldInstanceMethod|}("a");
                    }
                }
                """;
            var test = Create(source, source, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.InstanceWidget.OldInstanceMethod(System.String)" replacement="New.Stuff.NewHelper.DoThing" />
                </BannedSymbols>
                """);
            var diagnostic = Ban.WithReplacement("Legacy.Stuff.InstanceWidget.OldInstanceMethod(string)", "New.Stuff.NewHelper.DoThing").WithLocation(0);
            test.ExpectedDiagnostics.Add(diagnostic);
            test.FixedState.ExpectedDiagnostics.Add(diagnostic);
            await test.RunAsync();
        }

        [Fact]
        public async Task ArgumentMap_ReorderOnStaticMethodCall()
        {
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
                  <Ban symbol="Legacy.Stuff.OldHelper.Combine(System.String,System.String)" replacement="New.Stuff.NewHelper.Combine" argumentMap="1,0" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.Combine(string, string)", "New.Stuff.NewHelper.Combine").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task ArgumentMap_DropsArgument()
        {
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
                  <Ban symbol="Legacy.Stuff.OldHelper.TrimTo(System.String,System.Int32)" replacement="New.Stuff.NewHelper.TrimTo" argumentMap="0" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.TrimTo(string, int)", "New.Stuff.NewHelper.TrimTo").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task ArgumentMap_InvalidContent_IgnoredFallsBackToCountBasedBehavior()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        OldHelper.{|#0:DoThing|}("a");
                    }
                }
                """, """
                using New.Stuff;

                class C
                {
                    void M()
                    {
                        NewHelper.DoThing("a");
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper.DoThing(System.String)" replacement="New.Stuff.NewHelper.DoThing" argumentMap="not-a-number" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.DoThing(string)", "New.Stuff.NewHelper.DoThing").WithLocation(0));
            await test.RunAsync();
        }

        [Fact]
        public async Task ArgumentMap_OnConstructorSwap()
        {
            var test = Create("""
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = {|#0:new OldHelper(5, "a")|};
                    }
                }
                """, """
                using New.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new NewHelper("a", 5);
                    }
                }
                """, """
                <BannedSymbols>
                  <Ban symbol="Legacy.Stuff.OldHelper.#ctor(System.Int32,System.String)" replacement="New.Stuff.NewHelper" argumentMap="1,0" />
                </BannedSymbols>
                """);
            test.ExpectedDiagnostics.Add(
                Ban.WithReplacement("Legacy.Stuff.OldHelper.OldHelper(int, string)", "New.Stuff.NewHelper").WithLocation(0));
            await test.RunAsync();
        }
    }
}

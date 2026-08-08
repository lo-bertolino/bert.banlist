using System.Text;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis.Text;
using Xunit;

namespace Bert.Banlist.Tests
{
    public class RefactoringTests
    {
        [Fact]
        public async Task ExistingBanList_AppendsEntryWithTodoReplacement()
        {
            var source = """
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new [|OldHelper|]();
                    }
                }
                """;
            var test = new BanRefactoringTest { TestCode = source, FixedCode = source };
            test.TestState.Sources.Add(TestSources.Definitions);
            test.FixedState.Sources.Add(TestSources.Definitions);
            test.TestState.AdditionalFiles.Add(("BannedSymbols.xml", """
                <BannedSymbols>
                  <Ban kind="Type" symbol="Legacy.Data.Client" replacement="New.Data.Client" />
                </BannedSymbols>
                """));
            test.FixedState.AdditionalFiles.Add(("BannedSymbols.xml", SourceText.From(
                "<BannedSymbols>\n" +
                "  <Ban kind=\"Type\" symbol=\"Legacy.Data.Client\" replacement=\"New.Data.Client\" />\n" +
                "  <Ban kind=\"Type\" symbol=\"Legacy.Stuff.OldHelper\" replacement=\"TODO\" />\n" +
                "</BannedSymbols>\n", Encoding.UTF8)));
            await test.RunAsync();
        }

        [Fact]
        public async Task MethodSymbol_AppendsDocIdSignature()
        {
            var source = """
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        OldHelper.[|DoThing|]("a");
                    }
                }
                """;
            var test = new BanRefactoringTest { TestCode = source, FixedCode = source };
            test.TestState.Sources.Add(TestSources.Definitions);
            test.FixedState.Sources.Add(TestSources.Definitions);
            test.TestState.AdditionalFiles.Add(("BannedSymbols.xml", "<BannedSymbols />"));
            test.FixedState.AdditionalFiles.Add(("BannedSymbols.xml", SourceText.From(
                "<BannedSymbols>\n" +
                "  <Ban kind=\"Method\" symbol=\"Legacy.Stuff.OldHelper.DoThing(System.String)\" replacement=\"TODO\" />\n" +
                "</BannedSymbols>\n", Encoding.UTF8)));
            await test.RunAsync();
        }

        [Fact]
        public async Task AlreadyBanned_NoRefactoringOffered()
        {
            var source = """
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new [|OldHelper|]();
                    }
                }
                """;
            var banXml = """
                <BannedSymbols>
                  <Ban kind="Type" symbol="Legacy.Stuff.OldHelper" replacement="New.Stuff.NewHelper" />
                </BannedSymbols>
                """;
            var test = new BanRefactoringTest { TestCode = source, FixedCode = source };
            test.TestState.Sources.Add(TestSources.Definitions);
            test.FixedState.Sources.Add(TestSources.Definitions);
            test.TestState.AdditionalFiles.Add(("BannedSymbols.xml", banXml));
            test.FixedState.AdditionalFiles.Add(("BannedSymbols.xml", banXml));
            await test.RunAsync();
        }

        [Fact]
        public async Task NoBanList_CreatesFileWithEntry()
        {
            var source = """
                using Legacy.Stuff;

                class C
                {
                    void M()
                    {
                        var x = new [|OldHelper|]();
                    }
                }
                """;
            var test = new BanRefactoringTest { TestCode = source, FixedCode = source };
            test.TestState.Sources.Add(TestSources.Definitions);
            test.FixedState.Sources.Add(TestSources.Definitions);
            test.FixedState.AdditionalFiles.Add(("BannedSymbols.xml", SourceText.From(
                "<BannedSymbols>\n" +
                "  <Ban kind=\"Type\" symbol=\"Legacy.Stuff.OldHelper\" replacement=\"TODO\" />\n" +
                "</BannedSymbols>\n", Encoding.UTF8)));
            await test.RunAsync();
        }
    }
}

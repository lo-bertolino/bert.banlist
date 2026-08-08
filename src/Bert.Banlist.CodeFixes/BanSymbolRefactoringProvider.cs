using System.Composition;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeRefactorings;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace Bert.Banlist
{
    /// <summary>
    /// "Ban '{symbol}'" lightbulb action on any type/member/namespace reference or declaration.
    /// Appends an entry with a TODO replacement to the project's BannedSymbols.xml (creating the
    /// file when missing); the edit shows up as a normal pending change the user reviews and saves.
    /// The replacement value is edited in the XML by hand — no GUI prompt in v1.
    /// </summary>
    [ExportCodeRefactoringProvider(LanguageNames.CSharp, Name = nameof(BanSymbolRefactoringProvider))]
    [Shared]
    public sealed class BanSymbolRefactoringProvider : CodeRefactoringProvider
    {
        public override async System.Threading.Tasks.Task ComputeRefactoringsAsync(CodeRefactoringContext context)
        {
            var document = context.Document;
            var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return;
            }

            var node = root.FindNode(context.Span);
            var semanticModel = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (semanticModel == null)
            {
                return;
            }

            var symbol = node is SimpleNameSyntax name
                ? semanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol
                : semanticModel.GetDeclaredSymbol(node, context.CancellationToken);
            if (symbol == null)
            {
                return;
            }

            symbol = ((symbol as IMethodSymbol)?.ReducedFrom ?? symbol).OriginalDefinition;
            var bannable = symbol is INamedTypeSymbol or IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol or INamespaceSymbol;
            if (!bannable)
            {
                return;
            }

            // The doc-comment ID body is exactly the format the analyzer resolves, so a banned
            // overload or generic type round-trips without ambiguity.
            var docId = symbol.GetDocumentationCommentId();
            if (docId == null || docId.Length < 3)
            {
                return;
            }

            var symbolText = docId.Substring(2);
            var display = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            var project = document.Project;
            var banDocument = project.AdditionalDocuments.FirstOrDefault(d => BanList.IsBanListFile(d.FilePath ?? d.Name));

            if (banDocument != null)
            {
                var text = await banDocument.GetTextAsync(context.CancellationToken).ConfigureAwait(false);
                var xml = text.ToString();
                if (BanList.Parse(xml).Any(e => e.Symbol == symbolText))
                {
                    return;
                }

                var documentId = banDocument.Id;
                context.RegisterRefactoring(CodeAction.Create(
                    $"Ban '{display}' (add to {BanList.FileName})",
                    _ => System.Threading.Tasks.Task.FromResult(
                        project.Solution.WithAdditionalDocumentText(documentId, AppendEntry(xml, symbolText))),
                    equivalenceKey: "BanSymbol:" + docId));
            }
            else
            {
                // No ban list yet: offer to create one in the current document's project. In
                // multi-targeted/shared setups that is the deliberate default — the file lands next
                // to the csproj the open document belongs to.
                context.RegisterRefactoring(CodeAction.Create(
                    $"Create {BanList.FileName} and ban '{display}'",
                    _ =>
                    {
                        var directory = project.FilePath == null ? null : Path.GetDirectoryName(project.FilePath);
                        var filePath = directory == null ? null : Path.Combine(directory, BanList.FileName);
                        var solution = project.Solution.AddAdditionalDocument(
                            DocumentId.CreateNewId(project.Id),
                            BanList.FileName,
                            AppendEntry("<BannedSymbols />", symbolText),
                            filePath: filePath);
                        return System.Threading.Tasks.Task.FromResult(solution);
                    },
                    equivalenceKey: "BanSymbolCreate:" + docId));
            }
        }

        private static SourceText AppendEntry(string xml, string symbol)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(xml);
            }
            catch (System.Xml.XmlException)
            {
                document = new XDocument(new XElement("BannedSymbols"));
            }

            var root = document.Root;
            if (root == null)
            {
                root = new XElement("BannedSymbols");
                document.Add(root);
            }

            root.Add(new XElement(
                "Ban",
                new XAttribute("symbol", symbol),
                new XAttribute("replacement", "TODO")));

            // Normalized to \n so output is deterministic across hosts.
            return SourceText.From(document.ToString().Replace("\r\n", "\n") + "\n", Encoding.UTF8);
        }
    }
}

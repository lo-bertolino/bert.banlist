using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.VisualStudio.Extensibility;
using Microsoft.VisualStudio.Extensibility.Commands;
using Microsoft.VisualStudio.Extensibility.Editor;
using Microsoft.VisualStudio.Extensibility.Shell;

namespace Bert.Banlist.VisualStudio
{
    /// <summary>
    /// "Ban symbol under caret…": reads the identifier at the caret, prompts for the symbol name,
    /// kind, replacement and reason, and appends the entry to the project's BannedSymbols.xml.
    ///
    /// The out-of-process SDK gives the extension the editor's text and caret but no semantic model,
    /// so the symbol name is a lexical best guess pre-filled into an editable prompt rather than a
    /// resolved symbol. That is the entire trade: the portable NuGet refactoring resolves the symbol
    /// exactly but cannot ask a question; this command can ask but has to guess.
    /// </summary>
    [VisualStudioContribution]
    internal sealed class BanSymbolCommand : Command
    {
        // guidSHLMainMenu / IDM_VS_CTXT_CODEWIN from vsshlids.h — the code editor context menu.
        private static readonly Guid ShellMainMenu = new("D309F791-903F-11D0-9EFC-00A0C911004F");
        private const uint CodeWindowContextMenu = 0x040D;

        public override CommandConfiguration CommandConfiguration => new("%BanSymbolCommand.DisplayName%")
        {
            TooltipText = "%BanSymbolCommand.TooltipText%",
            Placements = new[]
            {
                // Both placements on purpose: the context menu is the natural home, the Extensions
                // menu guarantees the command is reachable (and searchable via Ctrl+Q) regardless.
                CommandPlacement.VsctParent(ShellMainMenu, CodeWindowContextMenu, priority: 0x0100),
                CommandPlacement.KnownPlacements.ExtensionsMenu,
            },
        };

        public override async Task ExecuteCommandAsync(IClientContext context, CancellationToken cancellationToken)
        {
            var shell = this.Extensibility.Shell();

            var textView = await this.Extensibility.Editor().GetActiveTextViewAsync(context, cancellationToken);
            if (textView == null)
            {
                await shell.ShowPromptAsync(
                    "Open a code document and put the caret on a symbol first.",
                    PromptOptions.AlertConfirm,
                    cancellationToken);
                return;
            }

            var documentPath = textView.FilePath;
            var documentText = textView.Document.Text.CopyToString();
            var caret = textView.Selection.InsertionPosition.Offset;

            var reference = SymbolTextResolver.Resolve(documentText, caret);
            if (reference == null)
            {
                await shell.ShowPromptAsync(
                    "No identifier at the caret. Put the caret on a type or member name and try again.",
                    PromptOptions.AlertConfirm,
                    cancellationToken);
                return;
            }

            var projectDirectory = ProjectLocator.FindProjectDirectory(documentPath);
            if (projectDirectory == null)
            {
                await shell.ShowPromptAsync(
                    $"Could not find a project file above '{documentPath}'. The ban list lives next to the project file, so save the document inside a project first.",
                    PromptOptions.AlertConfirm,
                    cancellationToken);
                return;
            }

            var symbol = await shell.ShowPromptAsync(
                BuildSymbolPromptMessage(reference),
                new InputPromptOptions { DefaultText = reference.BestGuess, Title = "Ban symbol" },
                cancellationToken);
            if (string.IsNullOrWhiteSpace(symbol))
            {
                return;
            }

            symbol = symbol!.Trim();

            var kindIndex = await shell.ShowPromptAsync(
                $"What kind of symbol is '{symbol}'?",
                BuildKindPromptOptions(reference.KindGuess),
                cancellationToken);
            if (kindIndex < 0 || kindIndex >= Kinds.Length)
            {
                return;
            }

            var kind = Kinds[kindIndex];

            var banFilePath = Path.Combine(projectDirectory, BanList.FileName);
            var existingXml = File.Exists(banFilePath) ? File.ReadAllText(banFilePath) : null;
            if (BanXmlWriter.Contains(existingXml, symbol))
            {
                await shell.ShowPromptAsync(
                    $"'{symbol}' is already banned in {banFilePath}.",
                    PromptOptions.InformationConfirm,
                    cancellationToken);
                return;
            }

            var replacement = await shell.ShowPromptAsync(
                $"Replacement for '{symbol}' — the full name of what to use instead. Leave blank for a diagnostic-only ban; Esc cancels.",
                new InputPromptOptions { DefaultText = string.Empty, Title = "Replacement" },
                cancellationToken);
            if (replacement == null)
            {
                return;
            }

            var reason = await shell.ShowPromptAsync(
                "Reason shown in the diagnostic message (optional). Esc cancels.",
                new InputPromptOptions { DefaultText = string.Empty, Title = "Reason" },
                cancellationToken);
            if (reason == null)
            {
                return;
            }

            string updatedXml;
            try
            {
                updatedXml = BanXmlWriter.Append(existingXml, symbol, replacement, reason);
            }
            catch (FormatException e)
            {
                await shell.ShowPromptAsync(
                    $"{banFilePath} could not be updated: {e.Message}",
                    PromptOptions.ErrorConfirm,
                    cancellationToken);
                return;
            }

            try
            {
                File.WriteAllText(banFilePath, updatedXml);
            }
            catch (Exception e) when (e is IOException || e is UnauthorizedAccessException)
            {
                await shell.ShowPromptAsync(
                    $"Could not write {banFilePath}: {e.Message}",
                    PromptOptions.ErrorConfirm,
                    cancellationToken);
                return;
            }

            if (existingXml == null)
            {
                // A brand new file is not an AdditionalFiles item, and the workspace API cannot add
                // one with that item type — so say so instead of leaving a ban list nothing reads.
                await shell.ShowPromptAsync(
                    $"Created {banFilePath}. Add it to the project for the analyzer to see it:\r\n\r\n" +
                    "<ItemGroup>\r\n  <AdditionalFiles Include=\"BannedSymbols.xml\" />\r\n</ItemGroup>",
                    PromptOptions.InformationConfirm,
                    cancellationToken);
            }

            // Show the result — the closest thing to a confirmation the user can act on.
            await this.Extensibility.Documents().OpenTextDocumentAsync(new Uri(banFilePath), cancellationToken);
        }

        private static string BuildSymbolPromptMessage(SymbolReference reference)
        {
            var message =
                $"Symbol to ban, fully qualified (caret text: '{reference.CaretText}').";
            var alternatives = reference.Candidates.Skip(1).Take(4).ToArray();
            if (alternatives.Length > 0)
            {
                message += " Other candidates: " + string.Join(", ", alternatives) + ".";
            }

            return message;
        }

        /// <summary>Ban kinds offered by the picker, in menu order.</summary>
        private static readonly BanKind[] Kinds =
        {
            BanKind.Type, BanKind.Method, BanKind.Property, BanKind.Field, BanKind.Event, BanKind.Namespace,
        };

        // PromptOptions&lt;TResult&gt; requires a non-nullable value type, so choices carry their index
        // and -1 means dismissed.
        private static PromptOptions<int> BuildKindPromptOptions(BanKind guess)
        {
            var choices = new ChoiceResultCollection<int>();
            for (var i = 0; i < Kinds.Length; i++)
            {
                choices.Add(Kinds[i].ToString(), i);
            }

            var defaultIndex = Array.IndexOf(Kinds, guess);
            return new PromptOptions<int>(choices, defaultIndex < 0 ? 0 : defaultIndex, dismissedReturns: -1)
            {
                Title = "Ban kind",
            };
        }
    }
}

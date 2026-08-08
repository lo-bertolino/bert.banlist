using Microsoft.VisualStudio.Extensibility;

namespace Bert.Banlist.VisualStudio
{
    /// <summary>
    /// Out-of-process VisualStudio.Extensibility extension that adds the "Ban symbol under caret…"
    /// command. Everything else Bert.Banlist does ships in the NuGet analyzer package and stays
    /// IDE-agnostic; this extension exists only to put a real input dialog in front of the
    /// replacement value, which a NuGet-delivered code refactoring cannot do.
    /// </summary>
    [VisualStudioContribution]
    internal sealed class BanlistExtension : Extension
    {
        public override ExtensionConfiguration ExtensionConfiguration => new()
        {
            Metadata = new(
                id: "Bert.Banlist.VisualStudio.6f2c1e6a",
                version: this.ExtensionAssemblyVersion,
                publisherName: "Bert",
                displayName: "Bert.Banlist",
                description: "Ban the symbol under the caret from BannedSymbols.xml, with a prompt for the replacement and reason."),

            // InstallationTargetVersion is left at the SDK default of "[17.14,)" — the SDK refuses a
            // lower minimum. VS 2026 evaluates only the lower bound of that range, so this single
            // build installs on both VS 2022 17.14+ and VS 2026.
        };
    }
}

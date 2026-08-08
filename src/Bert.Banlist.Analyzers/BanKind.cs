namespace Bert.Banlist
{
    /// <summary>
    /// The kind of symbol a resolved ban entry turned out to be. Not part of BannedSymbols.xml —
    /// there is nothing to author here, it is inferred by trying to resolve a ban's <c>symbol</c>
    /// text as a type, then as a member, then falling back to a namespace-prefix match. Used
    /// internally to carry that classification from the analyzer to the code fix.
    /// </summary>
    public enum BanKind
    {
        Type,
        Method,
        Property,
        Field,
        Event,
        Namespace,
    }
}

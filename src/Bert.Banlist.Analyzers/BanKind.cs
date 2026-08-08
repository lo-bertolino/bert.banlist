namespace Bert.Banlist
{
    /// <summary>The kind of symbol a ban entry targets. Serialized as the <c>kind</c> attribute in BannedSymbols.xml.</summary>
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

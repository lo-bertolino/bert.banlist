namespace Bert.Banlist
{
    /// <summary>One entry of the ban list.</summary>
    public sealed class BanEntry
    {
        public BanEntry(BanKind kind, string symbol, string? replacement, string? reason)
        {
            Kind = kind;
            Symbol = symbol;
            Replacement = replacement;
            Reason = reason;
        }

        public BanKind Kind { get; }

        /// <summary>
        /// Symbol name. Types: full name, generic arity either as <c>List&lt;T&gt;</c> or doc-id
        /// form <c>List`1</c>. Methods: full name, optionally with a doc-id parameter list to pin a
        /// single overload; without parentheses all overloads by that name are banned. Constructors
        /// use the <c>#ctor</c> member name. Namespaces: full namespace name (bans everything within,
        /// including sub-namespaces).
        /// </summary>
        public string Symbol { get; }

        /// <summary>Full name of the suggested replacement symbol, or null for a plain ban.</summary>
        public string? Replacement { get; }

        /// <summary>Free-text rationale shown in the diagnostic, or null.</summary>
        public string? Reason { get; }
    }
}

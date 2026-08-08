namespace Bert.Banlist
{
    /// <summary>
    /// One entry of a code fix's rewritten argument list: keep the original argument at
    /// <see cref="Source"/>, optionally reshaped through <see cref="Template"/>.
    /// </summary>
    public sealed class BanParam
    {
        public BanParam(int source, string? template)
        {
            Source = source;
            Template = template;
        }

        /// <summary>Zero-based index into the original call's argument list.</summary>
        public int Source { get; }

        /// <summary>
        /// Optional template for the new argument's text. <c>{0}</c> is replaced with the original
        /// argument's expression text (trivia stripped); anything else in the template is copied
        /// literally, so <c>"async {0}"</c> turns a lambda into an async lambda and
        /// <c>"System.TimeSpan.FromMilliseconds({0})"</c> wraps a value in a factory call. Null means
        /// the argument is copied as-is. The code fix only checks that the substituted text parses as
        /// a valid expression — not that it type-checks against the replacement's parameter — so a
        /// template that doesn't make sense for its target still compiles to something, just not
        /// necessarily something correct; write and test one before shipping it in a shared ban list.
        /// </summary>
        public string? Template { get; }
    }

    /// <summary>One entry of the ban list.</summary>
    public sealed class BanEntry
    {
        public BanEntry(string symbol, string? replacement, string? reason, int[]? argumentMap = null, BanParam[]? argumentPlan = null)
        {
            Symbol = symbol;
            Replacement = replacement;
            Reason = reason;
            ArgumentMap = argumentMap;
            ArgumentPlan = argumentPlan;
        }

        /// <summary>
        /// Symbol name. Types: full name, generic arity either as <c>List&lt;T&gt;</c> or doc-id
        /// form <c>List`1</c>. Methods: full name, optionally with a doc-id parameter list to pin a
        /// single overload; without parentheses all overloads by that name are banned. Constructors
        /// use the <c>#ctor</c> member name. Namespaces: full namespace name (bans everything within,
        /// including sub-namespaces).
        /// <para>
        /// There is no separate "kind" — what this text resolves to in the compilation decides how
        /// it is treated: a type if it resolves as one, a member (method/property/field/event) if
        /// splitting off the last segment resolves a container type with a member by that name, and
        /// a namespace-prefix ban otherwise. A parenthesized signature or a trailing <c>#ctor</c>
        /// always means "method", since only methods and constructors have those.
        /// </para>
        /// </summary>
        public string Symbol { get; }

        /// <summary>Full name of the suggested replacement symbol, or null for a plain ban.</summary>
        public string? Replacement { get; }

        /// <summary>Free-text rationale shown in the diagnostic, or null.</summary>
        public string? Reason { get; }

        /// <summary>
        /// Optional zero-based indices into the original argument list, defining the new argument
        /// order/selection for the code fix (e.g. <c>[1, 0]</c> swaps two arguments, <c>[0]</c> drops
        /// everything after the first). Null when the entry has no <c>argumentMap</c> attribute or it
        /// failed to parse. Superseded by <see cref="ArgumentPlan"/> when both are present.
        /// </summary>
        public int[]? ArgumentMap { get; }

        /// <summary>
        /// Optional, richer replacement for <see cref="ArgumentMap"/>: an ordered list of
        /// (source index, template) pairs parsed from the ban's <c>&lt;Param&gt;</c> child elements,
        /// letting a fix reshape an argument's text rather than just reorder it. Null when the entry
        /// has no <c>&lt;Param&gt;</c> children or one of them failed to parse.
        /// </summary>
        public BanParam[]? ArgumentPlan { get; }
    }
}

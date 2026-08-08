using System.Text;

namespace Bert.Banlist
{
    /// <summary>
    /// Converts the user-friendly names allowed in BannedSymbols.xml into documentation-comment ID
    /// bodies that <see cref="Microsoft.CodeAnalysis.DocumentationCommentId"/> can resolve.
    /// </summary>
    internal static class DocId
    {
        /// <summary>
        /// Normalizes a type name: <c>Ns.List&lt;T&gt;</c> becomes <c>Ns.List`1</c>.
        /// Names already in doc-id form pass through unchanged.
        /// </summary>
        public static string NormalizeType(string name)
        {
            var open = name.IndexOf('<');
            if (open < 0)
            {
                return name;
            }

            var arity = 1;
            var depth = 0;
            for (var i = open; i < name.Length; i++)
            {
                switch (name[i])
                {
                    case '<':
                        depth++;
                        break;
                    case '>':
                        depth--;
                        break;
                    case ',' when depth == 1:
                        arity++;
                        break;
                }
            }

            return name.Substring(0, open) + "`" + arity;
        }

        /// <summary>
        /// Normalizes a member signature: strips whitespace in the parameter list and converts
        /// angle brackets of constructed generic parameter types to the doc-id <c>{}</c> form.
        /// <c>Foo.Bar(System.Collections.Generic.List&lt;System.String&gt;)</c> becomes
        /// <c>Foo.Bar(System.Collections.Generic.List{System.String})</c>.
        /// </summary>
        public static string NormalizeSignature(string name)
        {
            var open = name.IndexOf('(');
            if (open < 0)
            {
                return name;
            }

            var builder = new StringBuilder(name.Length);
            builder.Append(name, 0, open);
            for (var i = open; i < name.Length; i++)
            {
                var c = name[i];
                switch (c)
                {
                    case '<':
                        builder.Append('{');
                        break;
                    case '>':
                        builder.Append('}');
                        break;
                    default:
                        if (!char.IsWhiteSpace(c))
                        {
                            builder.Append(c);
                        }

                        break;
                }
            }

            return builder.ToString();
        }
    }
}

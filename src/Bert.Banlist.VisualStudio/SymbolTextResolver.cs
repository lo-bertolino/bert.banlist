using System;
using System.Collections.Generic;
using System.Linq;

namespace Bert.Banlist.VisualStudio
{
    /// <summary>
    /// The symbol reference the caret sits on, as recovered from raw document text.
    /// </summary>
    public sealed class SymbolReference
    {
        public SymbolReference(string caretText, IReadOnlyList<string> candidates, BanKind kindGuess)
        {
            CaretText = caretText;
            Candidates = candidates;
            KindGuess = kindGuess;
        }

        /// <summary>The dotted name exactly as it appears at the caret, e.g. <c>Console.WriteLine</c>.</summary>
        public string CaretText { get; }

        /// <summary>
        /// Fully-qualified guesses, best first. Purely lexical — there is no compilation behind this,
        /// so the first entry is a suggestion the user is expected to review, not a resolved symbol.
        /// </summary>
        public IReadOnlyList<string> Candidates { get; }

        /// <summary>Best guess at the ban kind; the user confirms it in the kind prompt.</summary>
        public BanKind KindGuess { get; }

        public string BestGuess => Candidates.Count > 0 ? Candidates[0] : CaretText;
    }

    /// <summary>
    /// Recovers a ban-list symbol name from document text and a caret offset.
    ///
    /// This is deliberately lexical: the VisualStudio.Extensibility out-of-process SDK exposes the
    /// editor's text and selection but no Roslyn semantic model, so a name cannot be *resolved* —
    /// only guessed from the file's <c>using</c> directives and namespace declaration. Every guess
    /// ends up as editable default text in a prompt.
    /// </summary>
    public static class SymbolTextResolver
    {
        public static SymbolReference? Resolve(string? text, int caretOffset)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var source = text!;
            var caret = Math.Max(0, Math.Min(caretOffset, source.Length));

            // Caret sitting immediately after an identifier still targets that identifier.
            if ((caret >= source.Length || !IsIdentifierChar(source[caret])) &&
                caret > 0 && IsIdentifierChar(source[caret - 1]))
            {
                caret--;
            }

            if (caret >= source.Length || !IsIdentifierChar(source[caret]))
            {
                return null;
            }

            var end = caret;
            while (end + 1 < source.Length && IsIdentifierChar(source[end + 1]))
            {
                end++;
            }

            var start = caret;
            while (start > 0 && IsIdentifierChar(source[start - 1]))
            {
                start--;
            }

            // Walk left through a dotted qualified name: `A.B.C` with the caret on `C` yields all of it.
            while (start > 1 && source[start - 1] == '.' && IsIdentifierChar(source[start - 2]))
            {
                var segmentEnd = start - 2;
                var segmentStart = segmentEnd;
                while (segmentStart > 0 && IsIdentifierChar(source[segmentStart - 1]))
                {
                    segmentStart--;
                }

                // A numeric literal before the dot (`1.ToString()`) is not a namespace qualifier.
                if (char.IsDigit(source[segmentStart]))
                {
                    break;
                }

                start = segmentStart;
            }

            var dotted = source.Substring(start, end - start + 1);
            if (dotted.Length == 0)
            {
                return null;
            }

            var context = FileContext.Parse(source);
            var kind = GuessKind(source, start, end, dotted);
            var candidates = BuildCandidates(dotted, context);
            return new SymbolReference(dotted, candidates, kind);
        }

        private static BanKind GuessKind(string source, int start, int end, string dotted)
        {
            var lineStart = source.LastIndexOf('\n', Math.Max(0, start - 1)) + 1;
            var lineEnd = source.IndexOf('\n', end);
            var line = (lineEnd < 0 ? source.Substring(lineStart) : source.Substring(lineStart, lineEnd - lineStart)).Trim();

            if (line.StartsWith("using ", StringComparison.Ordinal) || line.StartsWith("namespace ", StringComparison.Ordinal))
            {
                return BanKind.Namespace;
            }

            var next = end + 1;
            while (next < source.Length && (source[next] == ' ' || source[next] == '\t'))
            {
                next++;
            }

            if (next < source.Length && source[next] == '<')
            {
                // Skip a (possibly nested) type-argument list to see whether a call follows.
                var depth = 0;
                while (next < source.Length)
                {
                    if (source[next] == '<')
                    {
                        depth++;
                    }
                    else if (source[next] == '>')
                    {
                        depth--;
                        if (depth == 0)
                        {
                            next++;
                            break;
                        }
                    }
                    else if (source[next] == ';' || source[next] == '\n')
                    {
                        break;
                    }

                    next++;
                }

                while (next < source.Length && (source[next] == ' ' || source[next] == '\t'))
                {
                    next++;
                }
            }

            if (next < source.Length && source[next] == '(')
            {
                return BanKind.Method;
            }

            // `new Foo(...)` is a type reference for ban purposes (kind="Type" bans the type).
            return BanKind.Type;
        }

        private static IReadOnlyList<string> BuildCandidates(string dotted, FileContext context)
        {
            var head = dotted.Split('.')[0];
            var result = new List<string>();

            void Add(string value)
            {
                if (!string.IsNullOrWhiteSpace(value) && !result.Contains(value, StringComparer.Ordinal))
                {
                    result.Add(value);
                }
            }

            // 1. An alias is the only case we can expand exactly.
            if (context.Aliases.TryGetValue(head, out var aliasTarget))
            {
                Add(dotted.Length == head.Length
                    ? aliasTarget
                    : aliasTarget + dotted.Substring(head.Length));
            }

            // 2. Already qualified: the head segment roots one of the file's namespaces.
            var alreadyQualified = context.Usings.Any(u => u == head || u.StartsWith(head + ".", StringComparison.Ordinal))
                || context.StaticUsings.Any(u => u == head || u.StartsWith(head + ".", StringComparison.Ordinal))
                || (context.Namespace != null &&
                    (context.Namespace == head || context.Namespace.StartsWith(head + ".", StringComparison.Ordinal)));
            if (alreadyQualified)
            {
                Add(dotted);
            }

            // 3. `using static X.Y;` makes members of X.Y visible unqualified.
            foreach (var staticUsing in context.StaticUsings)
            {
                Add(staticUsing + "." + dotted);
            }

            // 4. Plain usings, shortest namespace first: `using System;` beats `using System.Text.Json;`
            //    for an unqualified `Console`, which is the common case worth optimizing.
            foreach (var ns in context.Usings.OrderBy(u => u.Count(c => c == '.')).ThenBy(u => context.Usings.IndexOf(u)))
            {
                Add(ns + "." + dotted);
            }

            // 5. The file's own namespace, then the bare text as written.
            if (context.Namespace != null)
            {
                Add(context.Namespace + "." + dotted);
            }

            Add(dotted);
            return result;
        }

        private static bool IsIdentifierChar(char c) => char.IsLetterOrDigit(c) || c == '_';

        /// <summary>The <c>using</c>/<c>namespace</c> context of a file, scanned lexically.</summary>
        private sealed class FileContext
        {
            public List<string> Usings { get; } = new List<string>();

            public List<string> StaticUsings { get; } = new List<string>();

            public Dictionary<string, string> Aliases { get; } = new Dictionary<string, string>(StringComparer.Ordinal);

            public string? Namespace { get; private set; }

            public static FileContext Parse(string source)
            {
                var context = new FileContext();
                foreach (var rawLine in source.Split('\n'))
                {
                    var line = rawLine.Trim().TrimEnd('\r').Trim();

                    if (context.Namespace == null && line.StartsWith("namespace ", StringComparison.Ordinal))
                    {
                        var name = line.Substring("namespace ".Length).Trim().TrimEnd(';', '{').Trim();
                        if (IsQualifiedName(name))
                        {
                            context.Namespace = name;
                        }

                        continue;
                    }

                    if (!line.StartsWith("using ", StringComparison.Ordinal) || !line.EndsWith(";", StringComparison.Ordinal))
                    {
                        continue;
                    }

                    var body = line.Substring("using ".Length, line.Length - "using ".Length - 1).Trim();
                    if (body.StartsWith("static ", StringComparison.Ordinal))
                    {
                        var target = body.Substring("static ".Length).Trim();
                        if (IsQualifiedName(target))
                        {
                            context.StaticUsings.Add(target);
                        }

                        continue;
                    }

                    var equals = body.IndexOf('=');
                    if (equals > 0)
                    {
                        var alias = body.Substring(0, equals).Trim();
                        var target = body.Substring(equals + 1).Trim();
                        if (IsIdentifier(alias) && IsQualifiedName(target))
                        {
                            context.Aliases[alias] = target;
                        }

                        continue;
                    }

                    if (IsQualifiedName(body))
                    {
                        context.Usings.Add(body);
                    }
                }

                return context;
            }

            private static bool IsIdentifier(string value)
                => value.Length > 0 && !char.IsDigit(value[0]) && value.All(IsIdentifierChar);

            private static bool IsQualifiedName(string value)
                => value.Length > 0 && value.Split('.').All(IsIdentifier);
        }
    }
}

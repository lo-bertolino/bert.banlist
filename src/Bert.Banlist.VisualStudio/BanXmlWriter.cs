using System;
using System.Linq;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Bert.Banlist.VisualStudio
{
    /// <summary>
    /// Appends entries to BannedSymbols.xml as text.
    ///
    /// Text rather than <see cref="XDocument"/> round-tripping on purpose: this writes to a file on
    /// disk that the user already owns, so existing formatting, comments, indentation and line
    /// endings must survive untouched. Only the new entry is inserted.
    /// </summary>
    public static class BanXmlWriter
    {
        private const string RootName = "BannedSymbols";

        /// <summary>True when <paramref name="xml"/> already bans this exact kind/symbol pair.</summary>
        public static bool Contains(string? xml, BanKind kind, string symbol)
            => !string.IsNullOrWhiteSpace(xml)
               && BanList.Parse(xml!).Any(e => e.Kind == kind && string.Equals(e.Symbol, symbol, StringComparison.Ordinal));

        /// <summary>
        /// Returns <paramref name="xml"/> with one more <c>&lt;Ban /&gt;</c> entry. Null/blank input
        /// produces a fresh document. Empty <paramref name="replacement"/>/<paramref name="reason"/>
        /// are omitted, matching the schema's "attribute absent = plain ban".
        /// </summary>
        /// <exception cref="FormatException">
        /// The file exists but is not a well-formed ban list. Callers must surface this instead of
        /// overwriting: silently replacing a user's malformed file would lose its content.
        /// </exception>
        public static string Append(
            string? xml,
            BanKind kind,
            string symbol,
            string? replacement,
            string? reason,
            string defaultNewLine = "\r\n")
        {
            if (string.IsNullOrWhiteSpace(symbol))
            {
                throw new ArgumentException("Symbol must not be empty.", nameof(symbol));
            }

            var newLine = DetectNewLine(xml, defaultNewLine);
            var entry = RenderEntry(kind, symbol, replacement, reason);

            if (string.IsNullOrWhiteSpace(xml))
            {
                return "<" + RootName + ">" + newLine +
                       "  " + entry + newLine +
                       "</" + RootName + ">" + newLine;
            }

            var content = xml!;
            XDocument document;
            try
            {
                document = XDocument.Parse(content);
            }
            catch (XmlException e)
            {
                throw new FormatException("BannedSymbols.xml is not well-formed XML: " + e.Message, e);
            }

            if (document.Root == null || document.Root.Name.LocalName != RootName)
            {
                throw new FormatException("BannedSymbols.xml must have a <" + RootName + "> root element.");
            }

            var closing = content.LastIndexOf("</" + RootName, StringComparison.Ordinal);
            if (closing >= 0)
            {
                var indent = DetectIndent(content, closing);
                var insertion = indent + entry + newLine + LeadingWhitespaceOfLine(content, closing);
                return content.Substring(0, LineStart(content, closing)) + insertion + content.Substring(closing);
            }

            // Self-closing root: <BannedSymbols /> — expand it around the new entry.
            var selfClosing = FindSelfClosingRoot(content);
            if (selfClosing.start >= 0)
            {
                return content.Substring(0, selfClosing.start) +
                       "<" + RootName + ">" + newLine +
                       "  " + entry + newLine +
                       "</" + RootName + ">" +
                       content.Substring(selfClosing.start + selfClosing.length);
            }

            throw new FormatException("Could not find the <" + RootName + "> element to append to.");
        }

        private static string RenderEntry(BanKind kind, string symbol, string? replacement, string? reason)
        {
            var builder = new StringBuilder();
            builder.Append("<Ban kind=\"").Append(kind).Append('"');
            builder.Append(" symbol=\"").Append(Escape(symbol.Trim())).Append('"');
            if (!string.IsNullOrWhiteSpace(replacement))
            {
                builder.Append(" replacement=\"").Append(Escape(replacement!.Trim())).Append('"');
            }

            if (!string.IsNullOrWhiteSpace(reason))
            {
                builder.Append(" reason=\"").Append(Escape(reason!.Trim())).Append('"');
            }

            builder.Append(" />");
            return builder.ToString();
        }

        private static string Escape(string value)
            => value.Replace("&", "&amp;")
                    .Replace("<", "&lt;")
                    .Replace(">", "&gt;")
                    .Replace("\"", "&quot;");

        internal static string DetectNewLine(string? xml, string fallback)
        {
            if (string.IsNullOrEmpty(xml))
            {
                return fallback;
            }

            var index = xml!.IndexOf('\n');
            if (index < 0)
            {
                return fallback;
            }

            return index > 0 && xml[index - 1] == '\r' ? "\r\n" : "\n";
        }

        /// <summary>Indentation of the last existing entry, so appended entries line up with them.</summary>
        private static string DetectIndent(string content, int closingTagIndex)
        {
            var lastEntry = content.LastIndexOf("<Ban ", closingTagIndex, StringComparison.Ordinal);
            return lastEntry >= 0
                ? LeadingWhitespaceOfLine(content, lastEntry)
                : LeadingWhitespaceOfLine(content, closingTagIndex) + "  ";
        }

        private static int LineStart(string content, int index)
        {
            var newLine = content.LastIndexOf('\n', Math.Max(0, index - 1));
            return newLine < 0 ? 0 : newLine + 1;
        }

        private static string LeadingWhitespaceOfLine(string content, int index)
        {
            var start = LineStart(content, index);
            var end = start;
            while (end < content.Length && (content[end] == ' ' || content[end] == '\t'))
            {
                end++;
            }

            return content.Substring(start, end - start);
        }

        private static (int start, int length) FindSelfClosingRoot(string content)
        {
            var start = content.IndexOf("<" + RootName, StringComparison.Ordinal);
            if (start < 0)
            {
                return (-1, 0);
            }

            var end = content.IndexOf("/>", start, StringComparison.Ordinal);
            return end < 0 ? (-1, 0) : (start, end - start + 2);
        }
    }
}

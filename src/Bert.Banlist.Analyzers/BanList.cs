using System;
using System.Collections.Immutable;
using System.IO;
using System.Linq;
using System.Xml;
using System.Xml.Linq;

namespace Bert.Banlist
{
    /// <summary>Parsing of the BannedSymbols.xml additional file.</summary>
    public static class BanList
    {
        public const string FileName = "BannedSymbols.xml";

        public static bool IsBanListFile(string? path)
            => path != null && string.Equals(Path.GetFileName(path), FileName, StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// Parses ban entries from XML. Malformed XML or invalid entries are skipped silently:
        /// a broken config must never turn into analyzer crashes or compile errors.
        /// </summary>
        public static ImmutableArray<BanEntry> Parse(string xml)
        {
            XDocument document;
            try
            {
                document = XDocument.Parse(xml);
            }
            catch (XmlException)
            {
                return ImmutableArray<BanEntry>.Empty;
            }

            var root = document.Root;
            if (root == null)
            {
                return ImmutableArray<BanEntry>.Empty;
            }

            var builder = ImmutableArray.CreateBuilder<BanEntry>();
            foreach (var element in root.Elements("Ban"))
            {
                var symbol = (string?)element.Attribute("symbol");
                if (string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                var replacement = NullIfEmpty((string?)element.Attribute("replacement"));
                var reason = NullIfEmpty((string?)element.Attribute("reason"));
                var argumentMap = ParseArgumentMap((string?)element.Attribute("argumentMap"));
                var argumentPlan = ParseArgumentPlan(element);
                builder.Add(new BanEntry(symbol!.Trim(), replacement, reason, argumentMap, argumentPlan));
            }

            return builder.ToImmutable();
        }

        private static string? NullIfEmpty(string? value)
        {
            value = value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }

        /// <summary>
        /// Parses a comma-separated list of non-negative zero-based indices. Any malformed value
        /// (non-numeric, negative, empty entry) makes the whole attribute void rather than fail the
        /// build — same leniency philosophy as the rest of this parser.
        /// </summary>
        private static int[]? ParseArgumentMap(string? value)
        {
            value = value?.Trim();
            if (string.IsNullOrEmpty(value))
            {
                return null;
            }

            var parts = value!.Split(',');
            var indices = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i].Trim(), out var index) || index < 0)
                {
                    return null;
                }

                indices[i] = index;
            }

            return indices;
        }

        /// <summary>
        /// Parses ordered <c>&lt;Param source="N" [template="..."] /&gt;</c> children into the new
        /// argument list a code fix builds for a call site. A missing or invalid <c>source</c> on any
        /// one of them voids the whole plan for that ban, same leniency as the rest of this parser.
        /// </summary>
        private static BanParam[]? ParseArgumentPlan(XElement banElement)
        {
            var paramElements = banElement.Elements("Param").ToList();
            if (paramElements.Count == 0)
            {
                return null;
            }

            var result = new BanParam[paramElements.Count];
            for (var i = 0; i < paramElements.Count; i++)
            {
                var sourceText = (string?)paramElements[i].Attribute("source");
                if (!int.TryParse(sourceText, out var source) || source < 0)
                {
                    return null;
                }

                result[i] = new BanParam(source, NullIfEmpty((string?)paramElements[i].Attribute("template")));
            }

            return result;
        }
    }
}

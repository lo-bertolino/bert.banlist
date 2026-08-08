using System;
using System.Collections.Immutable;
using System.IO;
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
                var kindText = (string?)element.Attribute("kind");
                var symbol = (string?)element.Attribute("symbol");
                if (string.IsNullOrWhiteSpace(kindText) || string.IsNullOrWhiteSpace(symbol))
                {
                    continue;
                }

                if (!Enum.TryParse<BanKind>(kindText, ignoreCase: true, out var kind))
                {
                    continue;
                }

                var replacement = NullIfEmpty((string?)element.Attribute("replacement"));
                var reason = NullIfEmpty((string?)element.Attribute("reason"));
                builder.Add(new BanEntry(kind, symbol!.Trim(), replacement, reason));
            }

            return builder.ToImmutable();
        }

        private static string? NullIfEmpty(string? value)
        {
            value = value?.Trim();
            return string.IsNullOrEmpty(value) ? null : value;
        }
    }
}

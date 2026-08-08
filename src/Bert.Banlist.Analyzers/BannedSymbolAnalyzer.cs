using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace Bert.Banlist
{
    [DiagnosticAnalyzer(LanguageNames.CSharp)]
    public sealed class BannedSymbolAnalyzer : DiagnosticAnalyzer
    {
        public const string DiagnosticId = "BAN0001";

        // Keys of the diagnostic property bag the code fix reads. Keeping everything the fix needs
        // in the bag means the fix never has to re-parse BannedSymbols.xml.
        public const string KindProperty = "Kind";
        public const string BannedSymbolProperty = "BannedSymbol";
        public const string ReplacementProperty = "Replacement";
        public const string ReasonProperty = "Reason";
        public const string ArgumentMapProperty = "ArgumentMap";

        private const string Title = "Symbol is banned by style guide";
        private const string Category = "Style";

        private static readonly DiagnosticDescriptor s_ruleWithReplacement = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            "'{0}' is banned by style guide — use '{1}' instead.{2}",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The symbol is listed in this project's BannedSymbols.xml. Use the suggested replacement, or update the ban list.");

        private static readonly DiagnosticDescriptor s_ruleWithoutReplacement = new DiagnosticDescriptor(
            DiagnosticId,
            Title,
            "'{0}' is banned by style guide.{1}",
            Category,
            DiagnosticSeverity.Warning,
            isEnabledByDefault: true,
            description: "The symbol is listed in this project's BannedSymbols.xml.");

        public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics
            => ImmutableArray.Create(s_ruleWithReplacement, s_ruleWithoutReplacement);

        public override void Initialize(AnalysisContext context)
        {
            context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
            context.EnableConcurrentExecution();
            context.RegisterCompilationStartAction(OnCompilationStart);
        }

        private static void OnCompilationStart(CompilationStartAnalysisContext context)
        {
            var file = context.Options.AdditionalFiles.FirstOrDefault(f => BanList.IsBanListFile(f.Path));
            var text = file?.GetText(context.CancellationToken);
            if (text == null)
            {
                return;
            }

            // Parsed and resolved once per compilation; the node actions below only do dictionary lookups.
            var entries = BanList.Parse(text.ToString());
            if (entries.IsEmpty)
            {
                return;
            }

            var data = BanData.Resolve(context.Compilation, entries);
            if (data.Symbols.Count == 0 && data.Namespaces.IsEmpty)
            {
                return;
            }

            context.RegisterSyntaxNodeAction(
                c => AnalyzeName(c, data),
                SyntaxKind.IdentifierName,
                SyntaxKind.GenericName);
            context.RegisterSyntaxNodeAction(
                c => AnalyzeObjectCreation(c, data),
                SyntaxKind.ObjectCreationExpression,
                SyntaxKind.ImplicitObjectCreationExpression);
        }

        private static void AnalyzeName(SyntaxNodeAnalysisContext context, BanData data)
        {
            var node = (SimpleNameSyntax)context.Node;
            if (node is IdentifierNameSyntax { IsVar: true } || IsUsingOrNamespaceName(node))
            {
                // `var` binds to the inferred type; banning the type must not squiggle the keyword.
                return;
            }

            var symbol = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol;
            if (symbol == null || symbol is INamespaceSymbol)
            {
                // Namespace segments of qualified names are never reported directly; a namespace ban
                // is reported on the symbols used from it. Avoids one usage lighting up 3 squiggles.
                return;
            }

            var target = ((symbol as IMethodSymbol)?.ReducedFrom ?? symbol).OriginalDefinition;
            if (data.Symbols.TryGetValue(target, out var entry))
            {
                Report(context, node.GetLocation(), symbol, entry);
                return;
            }

            // Attribute names resolve to the attribute constructor, not the type — map a banned
            // attribute type back onto its name node.
            if (target is IMethodSymbol { MethodKind: MethodKind.Constructor } ctor
                && data.Symbols.TryGetValue(ctor.ContainingType.OriginalDefinition, out var typeEntry))
            {
                Report(context, node.GetLocation(), ctor.ContainingType, typeEntry);
                return;
            }

            if (!data.Namespaces.IsEmpty)
            {
                CheckNamespaceBan(context, node, symbol, data);
            }
        }

        private static void CheckNamespaceBan(SyntaxNodeAnalysisContext context, SimpleNameSyntax node, ISymbol symbol, BanData data)
        {
            // For `BannedNs.Type.Member`, the Type identifier already gets the diagnostic; skip the
            // member name so a single usage is squiggled once.
            if (symbol is not INamedTypeSymbol
                && node.Parent is MemberAccessExpressionSyntax memberAccess
                && memberAccess.Name == node
                && context.SemanticModel.GetSymbolInfo(memberAccess.Expression, context.CancellationToken).Symbol is INamedTypeSymbol)
            {
                return;
            }

            var containingNamespace = GetDeclaringNamespace(symbol);
            if (containingNamespace == null || containingNamespace.IsGlobalNamespace)
            {
                return;
            }

            if (TryMatchNamespace(containingNamespace, data, out var entry))
            {
                Report(context, node.GetLocation(), symbol, entry!);
            }
        }

        /// <summary>
        /// The namespace a symbol is <em>declared in</em>, for symbol kinds where referencing the
        /// symbol counts as using that namespace: types and their members. Returns null for
        /// everything else.
        /// <para>
        /// Locals, parameters, type parameters, labels and range variables inherit
        /// <see cref="ISymbol.ContainingNamespace"/> from the enclosing declaration, so treating them
        /// as usages would flag every local of every file that merely <em>lives inside</em> a banned
        /// namespace — which is exactly the code a team is trying to migrate away, not code that
        /// consumes it.
        /// </para>
        /// </summary>
        private static INamespaceSymbol? GetDeclaringNamespace(ISymbol symbol) => symbol switch
        {
            INamedTypeSymbol type => type.ContainingNamespace,
            IMethodSymbol or IPropertySymbol or IFieldSymbol or IEventSymbol => symbol.ContainingType?.ContainingNamespace,
            _ => null,
        };

        private static bool TryMatchNamespace(INamespaceSymbol containingNamespace, BanData data, out BanEntry? entry)
        {
            var namespaceName = containingNamespace.ToDisplayString();
            foreach (var (banned, candidate) in data.Namespaces)
            {
                if (namespaceName == banned || namespaceName.StartsWith(banned + ".", StringComparison.Ordinal))
                {
                    entry = candidate;
                    return true;
                }
            }

            entry = null;
            return false;
        }

        private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext context, BanData data)
        {
            if (context.SemanticModel.GetSymbolInfo(context.Node, context.CancellationToken).Symbol is not IMethodSymbol ctor)
            {
                return;
            }

            // Constructor-specific bans (kind="Method", member #ctor). Type-level bans on explicit
            // `new T(...)` are caught by the type name inside the expression via AnalyzeName.
            if (data.Symbols.TryGetValue(ctor.OriginalDefinition, out var entry))
            {
                Report(context, context.Node.GetLocation(), ctor, entry);
                return;
            }

            // Implicit `new(...)` spells no type name, so type and namespace bans must be checked here.
            if (context.Node is ImplicitObjectCreationExpressionSyntax)
            {
                var type = ctor.ContainingType;
                if (data.Symbols.TryGetValue(type.OriginalDefinition, out var typeEntry))
                {
                    Report(context, context.Node.GetLocation(), type, typeEntry);
                    return;
                }

                if (!data.Namespaces.IsEmpty
                    && type.ContainingNamespace is { IsGlobalNamespace: false } ns
                    && TryMatchNamespace(ns, data, out var nsEntry))
                {
                    Report(context, context.Node.GetLocation(), type, nsEntry!);
                }
            }
        }

        private static void Report(SyntaxNodeAnalysisContext context, Location location, ISymbol symbol, BanEntry entry)
        {
            var properties = ImmutableDictionary.CreateBuilder<string, string?>();
            properties.Add(KindProperty, entry.Kind.ToString());
            properties.Add(BannedSymbolProperty, entry.Symbol);
            if (entry.Replacement != null)
            {
                properties.Add(ReplacementProperty, entry.Replacement);
            }

            if (entry.Reason != null)
            {
                properties.Add(ReasonProperty, entry.Reason);
            }

            if (entry.ArgumentMap != null)
            {
                properties.Add(ArgumentMapProperty, string.Join(",", entry.ArgumentMap));
            }

            var display = symbol.ToDisplayString(SymbolDisplayFormat.CSharpErrorMessageFormat);
            var reasonSuffix = entry.Reason == null ? "" : " " + entry.Reason;
            var diagnostic = entry.Replacement != null
                ? Diagnostic.Create(s_ruleWithReplacement, location, properties.ToImmutable(), display, entry.Replacement, reasonSuffix)
                : Diagnostic.Create(s_ruleWithoutReplacement, location, properties.ToImmutable(), display, reasonSuffix);
            context.ReportDiagnostic(diagnostic);
        }

        private static bool IsUsingOrNamespaceName(SimpleNameSyntax node)
        {
            SyntaxNode top = node;
            while (top.Parent is NameSyntax)
            {
                top = top.Parent;
            }

            return top.Parent is UsingDirectiveSyntax || top.Parent is BaseNamespaceDeclarationSyntax;
        }

        /// <summary>Ban entries resolved against one compilation.</summary>
        private sealed class BanData
        {
            private BanData(Dictionary<ISymbol, BanEntry> symbols, ImmutableArray<(string Namespace, BanEntry Entry)> namespaces)
            {
                Symbols = symbols;
                Namespaces = namespaces;
            }

            /// <summary>Banned symbols keyed by original definition.</summary>
            public Dictionary<ISymbol, BanEntry> Symbols { get; }

            /// <summary>Banned namespace names; matched by prefix against containing namespaces.</summary>
            public ImmutableArray<(string Namespace, BanEntry Entry)> Namespaces { get; }

            public static BanData Resolve(Compilation compilation, ImmutableArray<BanEntry> entries)
            {
                var symbols = new Dictionary<ISymbol, BanEntry>(SymbolEqualityComparer.Default);
                var namespaces = ImmutableArray.CreateBuilder<(string, BanEntry)>();

                foreach (var entry in entries)
                {
                    switch (entry.Kind)
                    {
                        case BanKind.Namespace:
                            namespaces.Add((entry.Symbol, entry));
                            break;
                        case BanKind.Type:
                            AddAll(symbols, ResolveDocId("T:" + DocId.NormalizeType(entry.Symbol), compilation), entry);
                            break;
                        case BanKind.Method:
                            AddAll(symbols, ResolveMember(entry.Symbol, "M:", compilation, SymbolKind.Method), entry);
                            break;
                        case BanKind.Property:
                            AddAll(symbols, ResolveMember(entry.Symbol, "P:", compilation, SymbolKind.Property), entry);
                            break;
                        case BanKind.Field:
                            AddAll(symbols, ResolveMember(entry.Symbol, "F:", compilation, SymbolKind.Field), entry);
                            break;
                        case BanKind.Event:
                            AddAll(symbols, ResolveMember(entry.Symbol, "E:", compilation, SymbolKind.Event), entry);
                            break;
                    }
                }

                return new BanData(symbols, namespaces.ToImmutable());
            }

            private static void AddAll(Dictionary<ISymbol, BanEntry> map, IEnumerable<ISymbol> resolved, BanEntry entry)
            {
                foreach (var symbol in resolved)
                {
                    // First entry wins on duplicates.
                    if (!map.ContainsKey(symbol.OriginalDefinition))
                    {
                        map.Add(symbol.OriginalDefinition, entry);
                    }
                }
            }

            private static IEnumerable<ISymbol> ResolveDocId(string id, Compilation compilation)
                => DocumentationCommentId.GetSymbolsForDeclarationId(id, compilation);

            /// <summary>
            /// A member with a parenthesized signature resolves to that exact overload; without one,
            /// every member of the containing type with that name (and matching kind) is banned.
            /// Entries that do not resolve (assembly not referenced, typo) are silently skipped.
            /// </summary>
            private static IEnumerable<ISymbol> ResolveMember(string name, string prefix, Compilation compilation, SymbolKind kind)
            {
                if (name.IndexOf('(') >= 0)
                {
                    return ResolveDocId(prefix + DocId.NormalizeSignature(name), compilation);
                }

                var lastDot = name.LastIndexOf('.');
                if (lastDot <= 0 || lastDot == name.Length - 1)
                {
                    return Enumerable.Empty<ISymbol>();
                }

                var typeName = name.Substring(0, lastDot);
                var memberName = name.Substring(lastDot + 1);
                var members = new List<ISymbol>();
                foreach (var typeSymbol in ResolveDocId("T:" + DocId.NormalizeType(typeName), compilation))
                {
                    if (typeSymbol is not INamedTypeSymbol type)
                    {
                        continue;
                    }

                    if (memberName == "#ctor")
                    {
                        members.AddRange(type.InstanceConstructors);
                    }
                    else
                    {
                        members.AddRange(type.GetMembers(memberName).Where(m => m.Kind == kind));
                    }
                }

                return members;
            }
        }
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Composition;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CodeActions;
using Microsoft.CodeAnalysis.CodeFixes;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Editing;
using Microsoft.CodeAnalysis.Simplification;

namespace Bert.Banlist
{
    /// <summary>
    /// Replaces a banned symbol usage with the replacement configured in BannedSymbols.xml.
    /// A fix is only offered when the replacement resolves in the current compilation and — for
    /// invocations and constructor calls — an overload compatible with the existing argument list
    /// exists. Otherwise the diagnostic is left standing rather than producing broken code.
    /// </summary>
    [ExportCodeFixProvider(LanguageNames.CSharp, Name = nameof(BannedSymbolCodeFixProvider))]
    [Shared]
    public sealed class BannedSymbolCodeFixProvider : CodeFixProvider
    {
        public override ImmutableArray<string> FixableDiagnosticIds
            => ImmutableArray.Create(BannedSymbolAnalyzer.DiagnosticId);

        public override FixAllProvider GetFixAllProvider()
            => WellKnownFixAllProviders.BatchFixer;

        public override async Task RegisterCodeFixesAsync(CodeFixContext context)
        {
            var diagnostic = context.Diagnostics[0];
            if (!diagnostic.Properties.TryGetValue(BannedSymbolAnalyzer.ReplacementProperty, out var replacement)
                || string.IsNullOrWhiteSpace(replacement))
            {
                return;
            }

            if (!diagnostic.Properties.TryGetValue(BannedSymbolAnalyzer.KindProperty, out var kindText)
                || !Enum.TryParse<BanKind>(kindText, out var kind))
            {
                return;
            }

            diagnostic.Properties.TryGetValue(BannedSymbolAnalyzer.BannedSymbolProperty, out var bannedSymbol);

            var document = context.Document;
            var root = await document.GetSyntaxRootAsync(context.CancellationToken).ConfigureAwait(false);
            var semanticModel = await document.GetSemanticModelAsync(context.CancellationToken).ConfigureAwait(false);
            if (root == null || semanticModel == null)
            {
                return;
            }

            var node = root.FindNode(context.Span, getInnermostNodeForTie: true);
            var fix = kind switch
            {
                BanKind.Type => PrepareTypeFix(node, replacement!, semanticModel, context.CancellationToken),
                BanKind.Namespace => PrepareNamespaceFix(node, bannedSymbol, replacement!, semanticModel, context.CancellationToken),
                BanKind.Method => PrepareMethodFix(node, replacement!, semanticModel, context.CancellationToken),
                BanKind.Property or BanKind.Field or BanKind.Event => PrepareMemberFix(node, replacement!, semanticModel, context.CancellationToken),
                _ => null,
            };

            if (fix == null)
            {
                return;
            }

            // Stable per banned-symbol/replacement pair so the BatchFixer can merge identical fixes
            // across a document, project, or solution.
            var equivalenceKey = $"{BannedSymbolAnalyzer.DiagnosticId}:{kind}:{bannedSymbol}=>{replacement}";
            context.RegisterCodeFix(
                CodeAction.Create(
                    $"Replace with '{replacement}'",
                    cancellationToken => ApplyAsync(document, fix, cancellationToken),
                    equivalenceKey),
                diagnostic);
        }

        private sealed class PreparedFix
        {
            public PreparedFix(SyntaxNode target, string newNodeText, bool asExpression, string? staleNamespace)
            {
                Target = target;
                NewNodeText = newNodeText;
                AsExpression = asExpression;
                StaleNamespace = staleNamespace;
            }

            /// <summary>Node to replace (already widened to cover namespace qualifiers).</summary>
            public SyntaxNode Target { get; }

            /// <summary>Fully qualified (global::) text of the replacement node.</summary>
            public string NewNodeText { get; }

            /// <summary>Parse the replacement as an expression rather than a type name.</summary>
            public bool AsExpression { get; }

            /// <summary>Namespace of the banned symbol; its using directive is removed if unused after the fix.</summary>
            public string? StaleNamespace { get; }
        }

        private static PreparedFix? PrepareTypeFix(SyntaxNode node, string replacement, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (node is not SimpleNameSyntax typeNode)
            {
                return null;
            }

            return PrepareTypeReplacement(typeNode, replacement, semanticModel, cancellationToken);
        }

        private static PreparedFix? PrepareNamespaceFix(SyntaxNode node, string? bannedNamespace, string replacementNamespace, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            // Namespace-ban fixes only handle type references: the classic "same type moved to a new
            // namespace" migration (System.Data.SqlClient -> Microsoft.Data.SqlClient).
            if (node is not SimpleNameSyntax typeNode || bannedNamespace == null)
            {
                return null;
            }

            if (semanticModel.GetSymbolInfo(typeNode, cancellationToken).Symbol is not INamedTypeSymbol type)
            {
                return null;
            }

            var typeNamespace = type.ContainingNamespace?.ToDisplayString();
            if (typeNamespace == null)
            {
                return null;
            }

            string relative;
            if (typeNamespace == bannedNamespace)
            {
                relative = "";
            }
            else if (typeNamespace.StartsWith(bannedNamespace + ".", StringComparison.Ordinal))
            {
                relative = typeNamespace.Substring(bannedNamespace.Length);
            }
            else
            {
                return null;
            }

            return PrepareTypeReplacement(typeNode, replacementNamespace + relative + "." + type.Name, semanticModel, cancellationToken);
        }

        private static PreparedFix? PrepareTypeReplacement(SimpleNameSyntax typeNode, string replacementFullName, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            var arity = (typeNode as GenericNameSyntax)?.TypeArgumentList.Arguments.Count ?? 0;
            var metadataName = arity > 0 ? replacementFullName + "`" + arity : replacementFullName;
            var replacementType = semanticModel.Compilation.GetTypeByMetadataName(metadataName);
            if (replacementType == null)
            {
                // Replacement not resolvable in this compilation — leave the warning, don't break code.
                return null;
            }

            var target = WidenOverNamespaceQualifiers(typeNode, semanticModel, cancellationToken);

            // When the banned type is being constructed, only offer the swap if the replacement has a
            // constructor compatible with the existing argument list.
            var creation = target.Parent as ObjectCreationExpressionSyntax;
            if (creation != null && creation.Type == target)
            {
                var argumentCount = creation.ArgumentList?.Arguments.Count ?? 0;
                if (!replacementType.InstanceConstructors.Any(c => IsArityCompatible(c, argumentCount)))
                {
                    return null;
                }
            }

            var typeArguments = (typeNode as GenericNameSyntax)?.TypeArgumentList.ToString() ?? "";
            var asExpression = target.Parent is MemberAccessExpressionSyntax || target is MemberAccessExpressionSyntax;
            return new PreparedFix(
                target,
                "global::" + replacementFullName + typeArguments,
                asExpression,
                GetNamespaceName(semanticModel.GetSymbolInfo(typeNode, cancellationToken).Symbol));
        }

        private static PreparedFix? PrepareMethodFix(SyntaxNode node, string replacement, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            // Constructor-specific ban (kind="Method", #ctor): diagnostic sits on the whole `new` expression.
            if (node is ObjectCreationExpressionSyntax creation)
            {
                var argumentCount = creation.ArgumentList?.Arguments.Count ?? 0;
                var newType = semanticModel.Compilation.GetTypeByMetadataName(replacement);
                if (newType == null || !newType.InstanceConstructors.Any(c => IsArityCompatible(c, argumentCount)))
                {
                    return null;
                }

                var ctorSymbol = semanticModel.GetSymbolInfo(creation, cancellationToken).Symbol;
                return new PreparedFix(creation.Type, "global::" + replacement, asExpression: false, GetNamespaceName(ctorSymbol?.ContainingType));
            }

            if (node is not SimpleNameSyntax nameNode)
            {
                return null;
            }

            if (semanticModel.GetSymbolInfo(nameNode, cancellationToken).Symbol is not IMethodSymbol banned)
            {
                return null;
            }

            // v1 only rewrites static-style calls: `Type.Method(args)` -> `NewType.NewMethod(args)`.
            // Rewriting an instance call would drop the receiver.
            if (!banned.IsStatic)
            {
                return null;
            }

            var invocation = nameNode.Parent is MemberAccessExpressionSyntax access && access.Name == nameNode
                ? access.Parent as InvocationExpressionSyntax
                : nameNode.Parent as InvocationExpressionSyntax;
            if (invocation == null)
            {
                return null;
            }

            if (!TryResolveStaticMember(replacement, semanticModel.Compilation, out var members))
            {
                return null;
            }

            var argumentCount2 = invocation.ArgumentList.Arguments.Count;
            if (!members.OfType<IMethodSymbol>().Any(m => m.IsStatic && IsArityCompatible(m, argumentCount2)))
            {
                return null;
            }

            return new PreparedFix(invocation.Expression, "global::" + replacement, asExpression: true, GetNamespaceName(banned));
        }

        private static PreparedFix? PrepareMemberFix(SyntaxNode node, string replacement, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (node is not SimpleNameSyntax nameNode)
            {
                return null;
            }

            var banned = semanticModel.GetSymbolInfo(nameNode, cancellationToken).Symbol;
            if (banned == null || !banned.IsStatic)
            {
                // Same restriction as methods: instance member access has a receiver we can't rewrite.
                return null;
            }

            if (!TryResolveStaticMember(replacement, semanticModel.Compilation, out var members)
                || !members.Any(m => m.IsStatic && (m.Kind == SymbolKind.Property || m.Kind == SymbolKind.Field || m.Kind == SymbolKind.Event)))
            {
                return null;
            }

            SyntaxNode target = nameNode.Parent is MemberAccessExpressionSyntax access && access.Name == nameNode
                ? access
                : nameNode;
            return new PreparedFix(target, "global::" + replacement, asExpression: true, GetNamespaceName(banned));
        }

        /// <summary>
        /// Widens a banned type name to cover the namespace qualifiers around it, so
        /// `Legacy.Stuff.OldHelper` is replaced wholesale instead of producing `Legacy.Stuff.NewHelper`.
        /// Qualifiers that are types (nested types) are left alone.
        /// </summary>
        private static SyntaxNode WidenOverNamespaceQualifiers(SimpleNameSyntax typeNode, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            SyntaxNode target = typeNode;
            while (true)
            {
                var parent = target.Parent;
                if (parent is QualifiedNameSyntax qualified && qualified.Right == target
                    && semanticModel.GetSymbolInfo(qualified.Left, cancellationToken).Symbol is INamespaceSymbol)
                {
                    target = qualified;
                }
                else if (parent is MemberAccessExpressionSyntax memberAccess && memberAccess.Name == target
                    && semanticModel.GetSymbolInfo(memberAccess.Expression, cancellationToken).Symbol is INamespaceSymbol)
                {
                    target = memberAccess;
                }
                else if (parent is AliasQualifiedNameSyntax aliasQualified && aliasQualified.Name == target)
                {
                    target = aliasQualified;
                }
                else
                {
                    return target;
                }
            }
        }

        private static bool TryResolveStaticMember(string replacement, Compilation compilation, out ImmutableArray<ISymbol> members)
        {
            members = ImmutableArray<ISymbol>.Empty;
            var lastDot = replacement.LastIndexOf('.');
            if (lastDot <= 0 || lastDot == replacement.Length - 1)
            {
                return false;
            }

            var type = compilation.GetTypeByMetadataName(replacement.Substring(0, lastDot));
            if (type == null)
            {
                return false;
            }

            members = type.GetMembers(replacement.Substring(lastDot + 1));
            return !members.IsEmpty;
        }

        private static bool IsArityCompatible(IMethodSymbol method, int argumentCount)
        {
            var parameters = method.Parameters;
            var required = parameters.Count(p => !p.IsOptional && !p.IsParams);
            if (argumentCount < required)
            {
                return false;
            }

            return argumentCount <= parameters.Length || (parameters.Length > 0 && parameters[parameters.Length - 1].IsParams);
        }

        private static string? GetNamespaceName(ISymbol? symbol)
        {
            var ns = symbol is INamedTypeSymbol
                ? symbol.ContainingNamespace
                : symbol?.ContainingType?.ContainingNamespace ?? symbol?.ContainingNamespace;
            return ns is { IsGlobalNamespace: false } ? ns.ToDisplayString() : null;
        }

        private static async Task<Document> ApplyAsync(Document document, PreparedFix fix, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return document;
            }

            SyntaxNode newNode = fix.AsExpression
                ? SyntaxFactory.ParseExpression(fix.NewNodeText)
                : SyntaxFactory.ParseTypeName(fix.NewNodeText);
            newNode = newNode
                .WithTriviaFrom(fix.Target)
                .WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);

            var newDocument = document.WithSyntaxRoot(root.ReplaceNode(fix.Target, newNode));

            // Using-directive management: add the replacement's namespace, then drop the banned
            // symbol's using if nothing in the file needs it anymore.
            newDocument = await ImportAdder.AddImportsAsync(newDocument, Simplifier.AddImportsAnnotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            newDocument = await RemoveStaleUsingsAsync(newDocument, fix.StaleNamespace, cancellationToken).ConfigureAwait(false);
            newDocument = await Simplifier.ReduceAsync(newDocument, Simplifier.Annotation, cancellationToken: cancellationToken).ConfigureAwait(false);
            return newDocument;
        }

        private static async Task<Document> RemoveStaleUsingsAsync(Document document, string? staleNamespace, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(staleNamespace))
            {
                return document;
            }

            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return document;
            }

            var staleUsings = root.DescendantNodes()
                .OfType<UsingDirectiveSyntax>()
                .Where(u => u.Alias == null
                    && u.StaticKeyword.IsKind(SyntaxKind.None)
                    && u.Name?.ToString() == staleNamespace)
                .ToList();
            if (staleUsings.Count == 0)
            {
                return document;
            }

            var semanticModel = await document.GetSemanticModelAsync(cancellationToken).ConfigureAwait(false);
            if (semanticModel == null)
            {
                return document;
            }

            // Conservative: keep the using if any name outside a using directive still binds into
            // that namespace. Under a batch FixAll each individual fix still sees the other banned
            // usages, so the using survives until the last one — worst case an unused using is left,
            // never a broken file.
            foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
            {
                if (name.Ancestors().Any(a => a is UsingDirectiveSyntax))
                {
                    continue;
                }

                var symbol = semanticModel.GetSymbolInfo(name, cancellationToken).Symbol;
                if (symbol == null || symbol is INamespaceSymbol)
                {
                    continue;
                }

                var ns = symbol is INamedTypeSymbol
                    ? symbol.ContainingNamespace
                    : symbol.ContainingType?.ContainingNamespace;
                if (ns != null && !ns.IsGlobalNamespace && ns.ToDisplayString() == staleNamespace)
                {
                    return document;
                }
            }

            var newRoot = root.RemoveNodes(staleUsings, SyntaxRemoveOptions.KeepUnbalancedDirectives);
            return newRoot == null ? document : document.WithSyntaxRoot(newRoot);
        }
    }
}

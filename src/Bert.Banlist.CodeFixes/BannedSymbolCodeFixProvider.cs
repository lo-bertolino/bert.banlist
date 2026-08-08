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
    /// (or the entry's <c>argumentMap</c>, when present) exists. Otherwise the diagnostic is left
    /// standing rather than producing broken code.
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
            diagnostic.Properties.TryGetValue(BannedSymbolAnalyzer.ArgumentMapProperty, out var argumentMapText);
            var argumentMap = ParseArgumentMap(argumentMapText);

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
                BanKind.Method => PrepareMethodFix(node, replacement!, argumentMap, semanticModel, context.CancellationToken),
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
            public PreparedFix(SyntaxNode target, SyntaxNode replacement, string? staleNamespace, bool manageImports)
            {
                Target = target;
                Replacement = replacement;
                StaleNamespace = staleNamespace;
                ManageImports = manageImports;
            }

            /// <summary>Node to replace (already widened to cover namespace qualifiers, or the whole
            /// invocation/creation when the argument list is being rewritten too).</summary>
            public SyntaxNode Target { get; }

            /// <summary>Fully constructed replacement node, trivia and annotations already applied.</summary>
            public SyntaxNode Replacement { get; }

            /// <summary>Namespace of the banned symbol; its using directive is removed if unused after the fix.</summary>
            public string? StaleNamespace { get; }

            /// <summary>
            /// Whether the general using-directive machinery (ImportAdder, stale-using removal,
            /// Simplifier) should run. Pure member-name swaps on an unchanged receiver don't touch
            /// namespaces at all, so they skip this — a plain identifier swap is already complete.
            /// </summary>
            public bool ManageImports { get; }
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
            var text = "global::" + replacementFullName + typeArguments;
            SyntaxNode replacementNode = asExpression ? SyntaxFactory.ParseExpression(text) : SyntaxFactory.ParseTypeName(text);
            replacementNode = replacementNode
                .WithTriviaFrom(target)
                .WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);

            return new PreparedFix(
                target,
                replacementNode,
                GetNamespaceName(semanticModel.GetSymbolInfo(typeNode, cancellationToken).Symbol),
                manageImports: true);
        }

        private static PreparedFix? PrepareMethodFix(SyntaxNode node, string replacement, int[]? argumentMap, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            // Constructor-specific ban (kind="Method", #ctor): diagnostic sits on the whole `new` expression.
            if (node is ObjectCreationExpressionSyntax creation)
            {
                return PrepareConstructorFix(creation, replacement, argumentMap, semanticModel, cancellationToken);
            }

            if (node is not SimpleNameSyntax nameNode)
            {
                return null;
            }

            if (semanticModel.GetSymbolInfo(nameNode, cancellationToken).Symbol is not IMethodSymbol banned)
            {
                return null;
            }

            var access = nameNode.Parent as MemberAccessExpressionSyntax;
            var invocation = access != null && access.Name == nameNode
                ? access.Parent as InvocationExpressionSyntax
                : nameNode.Parent as InvocationExpressionSyntax;
            if (invocation == null)
            {
                return null;
            }

            if (!IsArgumentMapValidForCall(argumentMap, invocation.ArgumentList.Arguments.Count))
            {
                return null;
            }

            var effectiveArgCount = argumentMap?.Length ?? invocation.ArgumentList.Arguments.Count;

            // Reduced extension-method calls resolve to a symbol whose IsStatic is false even though
            // the declaration is static; check MethodKind first so this doesn't fall into the plain
            // instance path (or vice versa, depending on symbol quirks).
            if (banned.MethodKind == MethodKind.ReducedExtension)
            {
                // v1 does not rewrite extension-method calls — see README "Known limitations".
                // (Rewriting one requires resolving a replacement extension via the reduced receiver
                // and, when its namespace isn't imported, inserting a using — deliberately out of
                // scope for this pass; the diagnostic still fires, just no fix is offered.)
                return null;
            }

            if (banned.IsStatic)
            {
                if (!TryResolveMember(replacement, semanticModel.Compilation, out var members, out _))
                {
                    return null;
                }

                if (!members.OfType<IMethodSymbol>().Any(m => m.IsStatic && IsArityCompatible(m, effectiveArgCount)))
                {
                    return null;
                }

                return BuildInvocationFix(invocation, nameNode, "global::" + replacement, asFullExpression: true, argumentMap, GetNamespaceName(banned), manageImports: true);
            }

            // Genuine instance method: `recv.OldMethod(args)` -> `recv.NewMethod(args)`.
            if (access == null || access.Name != nameNode)
            {
                // No explicit receiver text to preserve (e.g. an unqualified call to an inherited
                // member from within the declaring type) — nothing safe to rewrite.
                return null;
            }

            var receiverType = semanticModel.GetTypeInfo(access.Expression, cancellationToken).Type;
            if (receiverType == null)
            {
                return null;
            }

            if (!TryResolveMember(replacement, semanticModel.Compilation, out var instanceMembers, out var replacementType) || replacementType == null)
            {
                return null;
            }

            if (!IsReceiverCompatible(receiverType, replacementType, semanticModel.Compilation))
            {
                return null;
            }

            if (!instanceMembers.OfType<IMethodSymbol>().Any(m => !m.IsStatic && IsArityCompatible(m, effectiveArgCount)))
            {
                return null;
            }

            var newMethodName = replacement.Substring(replacement.LastIndexOf('.') + 1);
            return BuildInvocationFix(invocation, nameNode, newMethodName, asFullExpression: false, argumentMap, staleNamespace: null, manageImports: false);
        }

        private static PreparedFix? PrepareConstructorFix(ObjectCreationExpressionSyntax creation, string replacement, int[]? argumentMap, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            var argumentCount = creation.ArgumentList?.Arguments.Count ?? 0;
            if (!IsArgumentMapValidForCall(argumentMap, argumentCount))
            {
                return null;
            }

            var effectiveArgCount = argumentMap?.Length ?? argumentCount;

            var newType = semanticModel.Compilation.GetTypeByMetadataName(replacement);
            if (newType == null || !newType.InstanceConstructors.Any(c => IsArityCompatible(c, effectiveArgCount)))
            {
                return null;
            }

            var ctorSymbol = semanticModel.GetSymbolInfo(creation, cancellationToken).Symbol;
            var staleNamespace = GetNamespaceName(ctorSymbol?.ContainingType);

            if (argumentMap == null || creation.ArgumentList == null)
            {
                var typeReplacement = SyntaxFactory.ParseTypeName("global::" + replacement)
                    .WithTriviaFrom(creation.Type)
                    .WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);
                return new PreparedFix(creation.Type, typeReplacement, staleNamespace, manageImports: true);
            }

            var newArgumentList = BuildMappedArgumentList(creation.ArgumentList, argumentMap);
            if (newArgumentList == null)
            {
                return null;
            }

            var newTypeNode = SyntaxFactory.ParseTypeName("global::" + replacement)
                .WithTriviaFrom(creation.Type)
                .WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);
            var newCreation = creation.WithType(newTypeNode).WithArgumentList(newArgumentList);
            return new PreparedFix(creation, newCreation, staleNamespace, manageImports: true);
        }

        private static PreparedFix? PrepareMemberFix(SyntaxNode node, string replacement, SemanticModel semanticModel, CancellationToken cancellationToken)
        {
            if (node is not SimpleNameSyntax nameNode)
            {
                return null;
            }

            var banned = semanticModel.GetSymbolInfo(nameNode, cancellationToken).Symbol;
            if (banned == null)
            {
                return null;
            }

            if (banned.IsStatic)
            {
                if (!TryResolveMember(replacement, semanticModel.Compilation, out var members, out _)
                    || !members.Any(m => m.IsStatic && m.Kind == banned.Kind))
                {
                    return null;
                }

                SyntaxNode target = nameNode.Parent is MemberAccessExpressionSyntax staticAccess && staticAccess.Name == nameNode
                    ? staticAccess
                    : nameNode;
                var replacementNode = SyntaxFactory.ParseExpression("global::" + replacement)
                    .WithTriviaFrom(target)
                    .WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);
                return new PreparedFix(target, replacementNode, GetNamespaceName(banned), manageImports: true);
            }

            // Genuine instance member (property/field/event): `recv.OldMember` -> `recv.NewMember`.
            if (nameNode.Parent is not MemberAccessExpressionSyntax access || access.Name != nameNode)
            {
                // No explicit receiver text to preserve — nothing safe to rewrite.
                return null;
            }

            var receiverType = semanticModel.GetTypeInfo(access.Expression, cancellationToken).Type;
            if (receiverType == null)
            {
                return null;
            }

            if (!TryResolveMember(replacement, semanticModel.Compilation, out var instanceMembers, out var replacementType) || replacementType == null)
            {
                return null;
            }

            if (!IsReceiverCompatible(receiverType, replacementType, semanticModel.Compilation))
            {
                return null;
            }

            if (!instanceMembers.Any(m => !m.IsStatic && m.Kind == banned.Kind))
            {
                return null;
            }

            var newMemberName = replacement.Substring(replacement.LastIndexOf('.') + 1);
            var newIdentifier = SyntaxFactory.IdentifierName(newMemberName).WithTriviaFrom(nameNode);
            return new PreparedFix(nameNode, newIdentifier, staleNamespace: null, manageImports: false);
        }

        /// <summary>
        /// Builds the fix for an invocation whose callee (and, when an argument map is present, its
        /// argument list) is being rewritten. Without a map only the callee node is touched — the
        /// existing argument list is left completely alone. With a map the whole invocation is
        /// replaced so the reordered argument list can be substituted at the same time.
        /// </summary>
        private static PreparedFix? BuildInvocationFix(
            InvocationExpressionSyntax invocation,
            SimpleNameSyntax nameNode,
            string newCalleeText,
            bool asFullExpression,
            int[]? argumentMap,
            string? staleNamespace,
            bool manageImports)
        {
            if (argumentMap == null)
            {
                SyntaxNode target = asFullExpression ? invocation.Expression : nameNode;
                SyntaxNode replacementNode = asFullExpression
                    ? SyntaxFactory.ParseExpression(newCalleeText).WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation)
                    : SyntaxFactory.IdentifierName(newCalleeText);
                replacementNode = replacementNode.WithTriviaFrom(target);
                return new PreparedFix(target, replacementNode, staleNamespace, manageImports);
            }

            var newArgumentList = BuildMappedArgumentList(invocation.ArgumentList, argumentMap);
            if (newArgumentList == null)
            {
                return null;
            }

            if (asFullExpression)
            {
                var newCallee = SyntaxFactory.ParseExpression(newCalleeText)
                    .WithTriviaFrom(invocation.Expression)
                    .WithAdditionalAnnotations(Simplifier.Annotation, Simplifier.AddImportsAnnotation);
                var newInvocation = invocation.WithExpression(newCallee).WithArgumentList(newArgumentList);
                return new PreparedFix(invocation, newInvocation, staleNamespace, manageImports);
            }

            // Instance / extension name-swap with a reordered argument list: only the member name and
            // the argument list change, the receiver expression is untouched.
            if (invocation.Expression is not MemberAccessExpressionSyntax access || access.Name != nameNode)
            {
                return null;
            }

            var newIdentifier = SyntaxFactory.IdentifierName(newCalleeText).WithTriviaFrom(nameNode);
            var newAccess = access.WithName(newIdentifier);
            var newInvocation2 = invocation.WithExpression(newAccess).WithArgumentList(newArgumentList);
            return new PreparedFix(invocation, newInvocation2, staleNamespace, manageImports);
        }

        /// <summary>
        /// Selects <paramref name="map"/> into <paramref name="original"/>'s arguments, preserving
        /// each argument's own syntax (named-argument colons, ref/out/in modifiers, expression trivia)
        /// but normalizing surrounding whitespace via freshly generated separators. Returns null when
        /// any index falls outside the actual call's argument list — the caller must skip the fix.
        /// </summary>
        private static ArgumentListSyntax? BuildMappedArgumentList(ArgumentListSyntax original, int[] map)
        {
            var args = original.Arguments;
            if (map.Any(i => i >= args.Count))
            {
                return null;
            }

            var newArgs = new ArgumentSyntax[map.Length];
            for (var i = 0; i < map.Length; i++)
            {
                newArgs[i] = args[map[i]].WithoutTrivia();
            }

            var separators = map.Length > 1
                ? Enumerable.Repeat(SyntaxFactory.Token(SyntaxKind.CommaToken).WithTrailingTrivia(SyntaxFactory.Space), map.Length - 1)
                : Enumerable.Empty<SyntaxToken>();

            return original.WithArguments(SyntaxFactory.SeparatedList(newArgs, separators));
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

        /// <summary>Resolves "Type.Member" into the containing type and its members named Member.</summary>
        private static bool TryResolveMember(string replacement, Compilation compilation, out ImmutableArray<ISymbol> members, out INamedTypeSymbol? containingType)
        {
            members = ImmutableArray<ISymbol>.Empty;
            containingType = null;
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

            containingType = type;
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

        /// <summary>
        /// True when every index falls inside the call's actual argument count. A null map always
        /// passes — no map means "compatibility is decided purely by count", the pre-existing rule.
        /// </summary>
        private static bool IsArgumentMapValidForCall(int[]? map, int actualArgumentCount)
            => map == null || map.All(i => i < actualArgumentCount);

        /// <summary>
        /// True when a value of <paramref name="receiverType"/> can be used where
        /// <paramref name="targetType"/> is expected without a cast — i.e. identical, a derived class,
        /// or an implemented interface. Value-type boxing conversions are deliberately excluded: they
        /// would silently change the call from a direct member access to a boxed interface dispatch.
        /// </summary>
        private static bool IsReceiverCompatible(ITypeSymbol receiverType, ITypeSymbol targetType, Compilation compilation)
        {
            if (SymbolEqualityComparer.Default.Equals(receiverType, targetType))
            {
                return true;
            }

            var conversion = compilation.ClassifyConversion(receiverType, targetType);
            return conversion.Exists && conversion.IsImplicit && (conversion.IsReference || conversion.IsIdentity);
        }

        private static string? GetNamespaceName(ISymbol? symbol)
        {
            var ns = symbol is INamedTypeSymbol
                ? symbol.ContainingNamespace
                : symbol?.ContainingType?.ContainingNamespace ?? symbol?.ContainingNamespace;
            return ns is { IsGlobalNamespace: false } ? ns.ToDisplayString() : null;
        }

        private static int[]? ParseArgumentMap(string? text)
        {
            if (string.IsNullOrEmpty(text))
            {
                return null;
            }

            var parts = text!.Split(',');
            var result = new int[parts.Length];
            for (var i = 0; i < parts.Length; i++)
            {
                if (!int.TryParse(parts[i], out var value) || value < 0)
                {
                    return null;
                }

                result[i] = value;
            }

            return result;
        }

        private static async Task<Document> ApplyAsync(Document document, PreparedFix fix, CancellationToken cancellationToken)
        {
            var root = await document.GetSyntaxRootAsync(cancellationToken).ConfigureAwait(false);
            if (root == null)
            {
                return document;
            }

            var newDocument = document.WithSyntaxRoot(root.ReplaceNode(fix.Target, fix.Replacement));

            if (!fix.ManageImports)
            {
                return newDocument;
            }

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

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Testing;

namespace Bert.Banlist.Tests
{
    public sealed class BanAnalyzerTest : CSharpAnalyzerTest<BannedSymbolAnalyzer, DefaultVerifier>
    {
        public BanAnalyzerTest() => ReferenceAssemblies = ReferenceAssemblies.Net.Net80;

        protected override ParseOptions CreateParseOptions()
            => ((CSharpParseOptions)base.CreateParseOptions()).WithLanguageVersion(LanguageVersion.Latest);
    }

    public sealed class BanCodeFixTest : CSharpCodeFixTest<BannedSymbolAnalyzer, BannedSymbolCodeFixProvider, DefaultVerifier>
    {
        public BanCodeFixTest() => ReferenceAssemblies = ReferenceAssemblies.Net.Net80;

        protected override ParseOptions CreateParseOptions()
            => ((CSharpParseOptions)base.CreateParseOptions()).WithLanguageVersion(LanguageVersion.Latest);
    }

    public sealed class BanRefactoringTest : CSharpCodeRefactoringTest<BanSymbolRefactoringProvider, DefaultVerifier>
    {
        public BanRefactoringTest() => ReferenceAssemblies = ReferenceAssemblies.Net.Net80;

        protected override ParseOptions CreateParseOptions()
            => ((CSharpParseOptions)base.CreateParseOptions()).WithLanguageVersion(LanguageVersion.Latest);
    }

    internal static class Ban
    {
        /// <summary>Diagnostic result for a ban with a replacement.</summary>
        public static DiagnosticResult WithReplacement(string display, string replacement, string? reason = null)
            => new DiagnosticResult(BannedSymbolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithMessage($"'{display}' is banned by style guide — use '{replacement}' instead.{Suffix(reason)}");

        /// <summary>Diagnostic result for a plain ban.</summary>
        public static DiagnosticResult Plain(string display, string? reason = null)
            => new DiagnosticResult(BannedSymbolAnalyzer.DiagnosticId, DiagnosticSeverity.Warning)
                .WithMessage($"'{display}' is banned by style guide.{Suffix(reason)}");

        private static string Suffix(string? reason) => reason == null ? "" : " " + reason;
    }
}

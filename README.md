# Bert.Banlist

A config-driven Roslyn analyzer that bans specific types, members, and namespaces from your C# code — including symbols from third-party assemblies you can't change — and, unlike `Microsoft.CodeAnalysis.BannedApiAnalyzers`, tells you *what to use instead* and fixes it for you:

- **`BAN0001` diagnostic** with the suggested replacement and reason in the message (like `[Obsolete]` gives).
- **Ctrl+. code fix** that applies the replacement, with Fix All support in Document / Project / Solution scope.
- **Using-directive management**: the fix adds the replacement's `using` and removes the banned symbol's `using` when nothing else in the file needs it.
- **"Ban this symbol" refactoring**: Ctrl+. on any symbol offers to add it to `BannedSymbols.xml` (creating the file if needed) as a normal pending edit you review and save.
- **XML ban list** (`BannedSymbols.xml`) shipped as an `AdditionalFiles` item — grow the list without recompiling anything.

Everything runs through the standard lightbulb menu, so it works in Visual Studio 2026, VS Code (C# Dev Kit), and Rider. No VSIX, no tool window.

## Install

```xml
<ItemGroup>
  <PackageReference Include="Bert.Banlist" Version="0.1.0" PrivateAssets="all" />
</ItemGroup>

<ItemGroup>
  <AdditionalFiles Include="BannedSymbols.xml" />
</ItemGroup>
```

`PrivateAssets="all"` keeps the analyzer from flowing transitively into consumers of your package.

The ban list is **per project**: each project's `BannedSymbols.xml` is independent, so different projects in one solution can carry different rules. Put a shared file in a common folder and `<AdditionalFiles Include="..\BannedSymbols.xml" />` from each project if you want one list everywhere.

## BannedSymbols.xml

```xml
<BannedSymbols>
  <!-- Type ban: sync commands are forbidden, use the async variant. -->
  <Ban kind="Type"
       symbol="CommunityToolkit.Mvvm.Input.RelayCommand"
       replacement="CommunityToolkit.Mvvm.Input.AsyncRelayCommand"
       reason="Commands must be async — see style guide §4." />

  <!-- Method ban, one specific overload (doc-id parameter list). -->
  <Ban kind="Method"
       symbol="System.Console.WriteLine(System.String)"
       replacement="MyProject.Logging.Log.Info"
       reason="Use structured logging." />

  <!-- Method ban, all overloads (no parameter list). -->
  <Ban kind="Method" symbol="System.Console.WriteLine" replacement="MyProject.Logging.Log.Info" />

  <!-- Constructor ban via the #ctor member name. -->
  <Ban kind="Method" symbol="System.Net.WebClient.#ctor" reason="WebClient is legacy; use HttpClient." />

  <!-- Namespace ban: everything in the namespace (and sub-namespaces). The fix maps each type
       to the same-named type under the replacement namespace when it exists. -->
  <Ban kind="Namespace"
       symbol="System.Data.SqlClient"
       replacement="Microsoft.Data.SqlClient"
       reason="See migration ADR-007." />

  <!-- Plain ban: no replacement, diagnostic only. -->
  <Ban kind="Property" symbol="System.DateTime.Now" reason="Use IClock — DateTime.Now is untestable." />
</BannedSymbols>
```

### Schema

| Attribute | Required | Meaning |
|---|---|---|
| `kind` | yes | `Type`, `Method`, `Property`, `Field`, `Event`, or `Namespace` |
| `symbol` | yes | Full name of the banned symbol (see formats below) |
| `replacement` | no | Full name of the suggested replacement. Present → shown in the message and the code fix is offered. Absent → plain ban, diagnostic only. |
| `reason` | no | Free text appended to the diagnostic message |

Symbol name formats:

- **Type**: `Some.Namespace.TypeName`. Generics either as `Some.Ns.MyList<T>` or doc-id form `Some.Ns.MyList` + `` `1 ``.
- **Method**: `Type.Method` bans every overload; `Type.Method(System.String, System.Int32)` (documentation-comment-ID parameter list) bans one overload. Constructors use `Type.#ctor` / `Type.#ctor(System.String)`.
- **Property / Field / Event**: `Some.Namespace.Type.MemberName`.
- **Namespace**: `Some.Namespace` — bans usage of everything within it, including sub-namespaces.

Entries that don't resolve against the current compilation (assembly not referenced, typo) are skipped silently — a shared ban list never breaks projects that don't reference the banned assembly. Malformed XML disables the analyzer for that project rather than erroring the build.

## Severity

Warning by default. Standard `.editorconfig` mechanism to override:

```ini
dotnet_diagnostic.BAN0001.severity = error
```

Generated code is not analyzed.

## When is the code fix offered?

The fix is deliberately conservative — a standing warning beats silently broken code:

- The replacement must resolve in the current compilation.
- Constructor calls: the replacement type needs a constructor with a compatible parameter count (optional/`params` aware). Otherwise no fix is offered.
- Method calls: only static-style calls (`Type.Method(args)`) are rewritten, argument list preserved as-is; a replacement overload with compatible arity must exist. Instance calls get the diagnostic but no fix (rewriting would drop the receiver).
- Properties/fields/events: same static-only rule.
- Namespace bans: fixed per type reference by mapping to the same-named type under the replacement namespace, when that type exists.

Note the compatibility check is arity-only: `RelayCommand(Action)` → `AsyncRelayCommand(Func<Task>)` both take one argument, so the fix is offered and the argument keeps its old type — you fix the resulting compile error (usually `() => ...` → `async () => ...`). That's intentional: it drags the call site to the new API instead of leaving it on the banned one.

## Banning a symbol from the editor

Put the caret on any type/member usage or declaration → Ctrl+. → **"Ban '…' (add to BannedSymbols.xml)"**. The entry is appended with `replacement="TODO"`; you edit the value in the XML directly (no GUI prompt in v1). If the project has no `BannedSymbols.xml` yet, the action offers to create it next to the csproj of the current document's project — remember to add the `<AdditionalFiles>` item if your project doesn't glob it.

## Known limitations (v1)

- The `using` cleanup keeps the old namespace's directive if *anything* in the file still binds to that namespace; under Fix All in large files it can occasionally leave an unused `using` (never a broken one) — `dotnet format` / IDE0005 picks those up.
- Instance-method/member replacements are not rewritten (see above).
- Argument compatibility is count-based only; no per-entry `argumentMap` yet.
- No GUI prompt for the replacement value when banning via the refactoring (would need a VS-specific dependency).

## Building

```
dotnet build
dotnet test
dotnet pack src/Bert.Banlist.Package/Bert.Banlist.Package.csproj -c Release -o artifacts
```

Layout: `Bert.Banlist.Analyzers.dll` (analyzer, no workspace dependency — loads in command-line builds) and `Bert.Banlist.CodeFixes.dll` (code fix + refactoring) both under `analyzers/dotnet/cs`, standard analyzer-package layout.

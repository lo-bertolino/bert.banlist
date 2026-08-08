# Bert.Banlist

A config-driven Roslyn analyzer that bans specific types, members, and namespaces from your C# code — including symbols from third-party assemblies you can't change — and, unlike `Microsoft.CodeAnalysis.BannedApiAnalyzers`, tells you *what to use instead* and fixes it for you:

- **`BAN0001` diagnostic** with the suggested replacement and reason in the message (like `[Obsolete]` gives).
- **Ctrl+. code fix** that applies the replacement, with Fix All support in Document / Project / Solution scope.
- **Using-directive management**: the fix adds the replacement's `using` and removes the banned symbol's `using` when nothing else in the file needs it.
- **"Ban this symbol" refactoring**: Ctrl+. on any symbol offers to add it to `BannedSymbols.xml` (creating the file if needed) as a normal pending edit you review and save.
- **XML ban list** (`BannedSymbols.xml`) shipped as an `AdditionalFiles` item — grow the list without recompiling anything.

The NuGet package runs entirely through the standard lightbulb menu, so it works in Visual Studio 2026, VS Code (C# Dev Kit), and Rider — no VSIX, no tool window. An [optional Visual Studio extension](#visual-studio-extension-optional-prompt-for-the-replacement) adds a command that *prompts* for the replacement instead of writing a `TODO`; installing it is never required.

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

  <!-- Method ban with argumentMap: the replacement takes the same two arguments in the opposite
       order. "1,0" means "new arg 0 = old arg 1, new arg 1 = old arg 0". -->
  <Ban kind="Method"
       symbol="Legacy.Utils.Pair(System.String,System.String)"
       replacement="MyProject.Utils.Pair"
       argumentMap="1,0" />

  <!-- Instance member ban: replacement lives on a base type / interface the receiver already
       implements, so the receiver expression is kept and only the member name changes. -->
  <Ban kind="Method"
       symbol="Legacy.Widgets.OldWidget.Render(System.String)"
       replacement="MyProject.Widgets.IWidget.RenderAsync"
       reason="Rendering must be async." />

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
| `argumentMap` | no | `Method`-kind entries only. Comma-separated zero-based indices into the *original* argument list, defining the *new* argument order/selection — see below. |

Symbol name formats:

- **Type**: `Some.Namespace.TypeName`. Generics either as `Some.Ns.MyList<T>` or doc-id form `Some.Ns.MyList` + `` `1 ``.
- **Method**: `Type.Method` bans every overload; `Type.Method(System.String, System.Int32)` (documentation-comment-ID parameter list) bans one overload. Constructors use `Type.#ctor` / `Type.#ctor(System.String)`.
- **Property / Field / Event**: `Some.Namespace.Type.MemberName`.
- **Namespace**: `Some.Namespace` — bans usage of everything within it, including sub-namespaces. "Usage" means referencing a type or a type's member: code that merely *lives inside* the banned namespace is not flagged for its own locals, parameters or type parameters, so you can ban a legacy namespace while its source still compiles in the same solution.

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
- Constructor calls: the replacement type needs a constructor with a compatible parameter count (optional/`params` aware, or compatible with the entry's `argumentMap` length — see below). Otherwise no fix is offered.
- Method calls, static-style (`Type.Method(args)`): rewritten to `NewType.NewMethod(args)`, argument list preserved as-is (or reordered per `argumentMap`); a replacement overload with compatible arity must exist.
- Method calls, instance-style (`recv.Method(args)`): rewritten to `recv.NewMethod(args)` — only the member name changes, the receiver expression is untouched. Offered only when the replacement resolves to another **instance** member, the receiver's type is identical to / derives from / implements the replacement's containing type, and an overload with compatible arity exists. A banned instance member with a static-only replacement (or vice versa) is left as a diagnostic — the shapes don't match, so no fix is offered.
- Properties/fields/events: same rule as methods — static bans need a static replacement (rewritten as `NewType.NewMember`), instance bans need a compatible instance replacement reachable from the receiver's type (rewritten as `recv.NewMember`).
- Extension-method calls (`recv.Ext(args)`) are not rewritten in v1 — the diagnostic still fires, but no fix is offered (see Known limitations).
- Namespace bans: fixed per type reference by mapping to the same-named type under the replacement namespace, when that type exists.

Note the compatibility check is arity-only: `RelayCommand(Action)` → `AsyncRelayCommand(Func<Task>)` both take one argument, so the fix is offered and the argument keeps its old type — you fix the resulting compile error (usually `() => ...` → `async () => ...`). That's intentional: it drags the call site to the new API instead of leaving it on the banned one.

### `argumentMap`

For `Method`-kind entries (including constructors), `argumentMap` reorders or drops arguments when the
replacement's parameter list doesn't line up 1:1 with the banned one. It's a comma-separated list of
zero-based indices into the *original* call's argument list; the *position* in the list is the new
argument's position, and the *value* is which original argument goes there:

- `argumentMap="1,0"` swaps two arguments: `Old(a, b)` → `New(b, a)`.
- `argumentMap="0"` keeps only the first argument, dropping the rest: `Old(a, b)` → `New(a)`.

Compatibility then becomes: every index must be within the call site's actual argument count, **and**
the replacement needs an overload whose arity matches `argumentMap`'s length (rather than the original
call's argument count). Each selected argument's own syntax — named-argument colon, `ref`/`out`/`in`
modifier, the expression itself — is carried over unchanged; only its position moves.

A malformed `argumentMap` (non-numeric, negative, or otherwise unparsable) is treated as if the
attribute were absent — the fix falls back to plain count-based compatibility instead of failing.

## Banning a symbol from the editor

Put the caret on any type/member usage or declaration → Ctrl+. → **"Ban '…' (add to BannedSymbols.xml)"**. The entry is appended with `replacement="TODO"`; you edit the value in the XML directly. If the project has no `BannedSymbols.xml` yet, the action offers to create it next to the csproj of the current document's project — remember to add the `<AdditionalFiles>` item if your project doesn't glob it.

This works everywhere — VS 2026, VS Code, Rider — because it's a plain Roslyn refactoring shipped in the NuGet package. Analyzer packages are loaded outside MEF and can't import host services, so a refactoring cannot open a dialog in any IDE. Getting a real prompt requires a Visual Studio extension, which is what the next section is.

## Visual Studio extension (optional): prompt for the replacement

`src/Bert.Banlist.VisualStudio` is a [VisualStudio.Extensibility](https://learn.microsoft.com/visualstudio/extensibility/visualstudio.extensibility/) out-of-process extension adding one command, **"Ban symbol under caret…"**, on the code editor context menu and under Extensions (so Ctrl+Q finds it). It is entirely optional and completely separate from the NuGet package — nothing in the analyzer, the code fix, or the portable refactoring depends on it.

UX flow, four prompts, Esc at any point cancels:

1. **Symbol** — pre-filled with a fully-qualified guess, editable.
2. **Kind** — `Type` / `Method` / `Property` / `Field` / `Event` / `Namespace`, defaulted to the guess.
3. **Replacement** — blank means a diagnostic-only ban (attribute omitted, not `TODO`).
4. **Reason** — blank means the attribute is omitted.

The entry is then appended to `BannedSymbols.xml` next to the nearest project file above the active document, preserving that file's existing entries, comments, indentation and line endings, and the file is opened so you see the result. Banning something already in the list says so instead of adding a duplicate. If the file had to be created, a prompt reminds you to add `<AdditionalFiles Include="BannedSymbols.xml" />` — the extension cannot add an item at that item type itself.

### Fidelity: the symbol name is a guess

The out-of-process SDK exposes the editor's text and caret but **no Roslyn semantic model**, so the extension cannot resolve the symbol the way the portable refactoring does. It reads the dotted name at the caret and guesses a fully-qualified form from the file's `using` directives and namespace: aliases and `using static` expand exactly, an already-qualified name is left alone, otherwise the shortest imported namespace wins (`using System;` beats `using System.Text.Json;` for a bare `Console`). Other candidates are listed in the prompt message, and the value is editable — the prompt *is* the correction mechanism.

Two consequences worth knowing:

- The kind is guessed from syntax only (`(` after the name → `Method`, inside a `using` line → `Namespace`, otherwise `Type`), which is why the kind picker is always shown rather than inferred silently.
- Method entries are written as `Type.Method`, which bans **every overload**. The doc-comment-ID parameter list that pins a single overload needs symbol resolution; add it by hand if you want one overload. The portable Ctrl+. refactoring gets this right because it has the semantic model.

Both limits are inherent to running out of process. The in-process VSSDK-compatible variant of the SDK could reach Roslyn, at the cost of a build that needs the full VS SDK toolchain.

### Building and installing it

```
dotnet build src/Bert.Banlist.VisualStudio/Bert.Banlist.VisualStudio.csproj
```

Plain `dotnet build` — no VS SDK build tools, no `devenv`, no MSBuild from a VS install. Output: `src/Bert.Banlist.VisualStudio/bin/Debug/net8.0/Bert.Banlist.VisualStudio.vsix`; double-click it to install. It is part of `Bert.Banlist.slnx`, so a plain solution-wide `dotnet build` builds it too.

The manifest's installation target is `[17.14,)` (the SDK's floor). Visual Studio 2026 evaluates only the lower bound of that range, so this one build installs on both VS 2022 17.14+ and VS 2026.

### Manual verification checklist (needs VS 2026 — not verifiable in CI)

Everything below the UI is unit-tested (`tests/Bert.Banlist.VisualStudio.Tests`), but the prompts, menu placement and editor plumbing can only be checked by running it:

1. Install the VSIX and restart VS 2026; confirm **Extensions → Ban symbol under caret…** exists and Ctrl+Q finds it.
2. Right-click in a C# editor: the command should appear in the context menu (the placement uses `IDM_VS_CTXT_CODEWIN`; if it is missing, the Extensions menu entry still works).
3. Caret on `Console.WriteLine("x")` in a file with `using System;` → symbol prompt pre-filled `System.Console.WriteLine`, kind defaulted to `Method`.
4. Enter a replacement and reason → `BannedSymbols.xml` opens with the new entry, existing entries and comments untouched, line endings unchanged.
5. Repeat the same symbol → "already banned" prompt, no duplicate entry.
6. Run it in a project with no ban list → file created next to the csproj plus the `AdditionalFiles` reminder; add the item, rebuild, confirm `BAN0001` fires.
7. Press Esc at each of the four prompts → nothing is written.
8. Caret on whitespace, and a document outside any project → the corresponding error prompts, no file written.

A single combined dialog (all four fields at once) is possible via the SDK's Remote UI `ShowDialogAsync`; sequential prompts were chosen because they need no XAML and were the only shape verifiable without VS 2026 installed.

## Known limitations (v1)

- The `using` cleanup keeps the old namespace's directive if *anything* in the file still binds to that namespace; under Fix All in large files it can occasionally leave an unused `using` (never a broken one) — `dotnet format` / IDE0005 picks those up.
- Instance-member replacements only rewrite the member name — the receiver expression is never
  changed, so the fix is only offered when the receiver's existing type already reaches the
  replacement (same type, base type, or implemented interface). If the migration requires a
  genuinely different receiver (e.g. wrapping the object, or calling a factory), no fix is offered.
- Extension-method calls (`recv.Ext(args)`) are not rewritten, even when a compatible replacement
  extension method exists — the diagnostic still fires, but you make the edit by hand.
- Argument compatibility beyond a plain count check is opt-in via `argumentMap`, and it only reorders
  or drops arguments — it can't synthesize a new argument value that wasn't in the original call.
- No GUI prompt for the replacement value when banning via the portable Ctrl+. refactoring — a NuGet-delivered refactoring can't reach host UI in any IDE. Install the optional [Visual Studio extension](#visual-studio-extension-optional-prompt-for-the-replacement) for a prompt; it trades exact symbol resolution for the dialog.

## Building

```
dotnet build
dotnet test
dotnet pack src/Bert.Banlist.Package/Bert.Banlist.Package.csproj -c Release -o artifacts
```

`dotnet build` also builds the optional Visual Studio extension into a `.vsix` (see above); it needs no VS SDK build tools and never affects the NuGet package.

Layout: `Bert.Banlist.Analyzers.dll` (analyzer, no workspace dependency — loads in command-line builds) and `Bert.Banlist.CodeFixes.dll` (code fix + refactoring) both under `analyzers/dotnet/cs`, standard analyzer-package layout.

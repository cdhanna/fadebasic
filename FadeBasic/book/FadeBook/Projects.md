# Project Guide

A FadeBasic project is a regular .NET console-app csproj (`<OutputType>Exe</OutputType>`) augmented with the **FadeBasic.Build** package, which compiles `.fbasic` source into a generated launchable C# class at build time. Adding **FadeBasic.Testing** on top of that opts the project into `dotnet test` and IDE Test Explorer integration without changing the executable shape — the same csproj answers both `dotnet run` and `dotnet test`.

This page enumerates every MSBuild property, item, and diagnostic those two packages introduce.

## A minimal project

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FadeBasic.Build" Version="0.0.2.512" />
    <PackageReference Include="FadeBasic.Lang.Core" Version="0.0.2.512" />
    <PackageReference Include="FadeBasic.Lib.Standard" Version="0.0.2.512" />

    <FadeCommand Include="FadeBasic.Lib.Standard"
                 FullName="FadeBasic.Lib.Standard.StandardCommands" />
    <FadeSource Include="main.fbasic" />
  </ItemGroup>
</Project>
```

To make this same project a `dotnet test` target, add **one** more `<PackageReference>`:

```xml
<PackageReference Include="FadeBasic.Testing" Version="0.0.2.512" />
```

Everything else is automatic: `<FadeEnableTesting>` defaults to `true` once the package is referenced, the project becomes an `IsTestProject`, the VSTest adapter DLL lands in `bin/`, and Test Explorer in Rider, VS Code C# Dev Kit, and Visual Studio picks up every `test ... endtest` block in your `.fbasic` source. `dotnet run` continues to work — the project is still a console app.

To opt back out without removing the reference (handy in samples that show the pattern but shouldn't actually be test projects), set `<FadeEnableTesting>false</FadeEnableTesting>`.

## FadeBasic.Build properties

These are evaluated by [FadeBasic.Build.props](../../FadeBuildTasks/FadeBasic.Build.props) and [FadeBasic.Build.targets](../../FadeBuildTasks/FadeBasic.Build.targets) when you reference `FadeBasic.Build`.

| Property | Default | What it does |
|---|---|---|
| **`FadeBasicDebug`** | `true` when `Configuration=Debug`, otherwise unset | Emits source-mapping debug data into the generated launchable so the DAP debugger can step through `.fbasic` source. Set to `false` in Debug to opt out, or `true` in Release to opt in. |
| **`FadeGenerateMain`** | `True` | Emits a `Main` method on the generated launchable class. Set to `False` if your project ships its own `Main` (e.g., a custom host like the MonoGame sample) and you want to call the generator's static helpers manually. |
| **`FadeIgnoreChecks`** | `False` | Skips the parser's semantic-error pass (unknown symbols, type mismatches, etc.). Useful when iterating quickly with deliberately-broken source; **don't ship with this on**. |
| **`FadeGeneratedLaunchType`** | `GeneratedFade` | Class name of the generated launchable. Rename if you have a name collision or want a more domain-specific identifier. |
| **`FadeGeneratedFolder`** | `Launch` | Subfolder under the project where the generated `.g.cs` lands. |
| **`FadeGeneratedLaunchFile`** | `$(MSBuildProjectDirectory)/$(FadeGeneratedFolder)/$(FadeGeneratedLaunchType).g.cs` | Full path to the generated file. Override only if you need an unusual layout; the default is what every code-navigation tool will look for. |
| **`FadeDisableAutoBuild`** | unset (i.e., auto-build is on) | Set to `true` to disable the `GenerateFadeBasic` MSBuild target entirely. Use this when you want to drive the generator by hand from a custom target. |
| **`FadeDisableAutoDocs`** | `false` | Skips the `GenerateFadeDocs` target that emits `FadeCommandDocs.md` from `[FadeBasicCommand]`-tagged methods. Set to `true` to keep your build quiet when you don't ship docs. |
| **`FadeDocsOutputFile`** | `$(IntermediateOutputPath)FadeCommandDocs.md` | Where the generated command-docs Markdown lands. |
| **`FadeEnableTesting`** | unset (read-only here) | The Build package only *reads* this — it controls which `Main` shape `FadeProjectTask` emits (the testing one routes args through `FadeTestApplicationBuilder`). The property is *assigned* by FadeBasic.Testing's own props (default `True` when that package is referenced). |
| **`FadeVersion`** | unset; usually set by your build pipeline | The Fade package version your project is targeting. Used in the FADE0002 diagnostic message and for transitive `FadeBasic.Testing` version pinning. |

## FadeBasic.Build items

| Item | What it is |
|---|---|
| **`<FadeSource Include="x.fbasic" />`** | A FadeBasic source file. Multiple `<FadeSource>` items concatenate (with a `SourceMap` so error locations and test source paths stay file-accurate). Order can matter for top-level `.fbasic` evaluation. |
| **`<FadeCommand Include="<assembly-name>" FullName="<fully-qualified-class>" />`** | A C# class that exposes `[FadeBasicCommand]`-tagged methods. The `Include` is the assembly the class lives in (must also be a `<PackageReference>` or `<ProjectReference>`); `FullName` is the class. The class's commands become callable from `.fbasic`. Items are hidden from the IDE's project view (`Visible=false`) — they're metadata, not files. |

## FadeBasic.Testing properties

These are set by [FadeBasic.Testing.props](../../FadeBasic.Testing/FadeBasic.Testing.props) the moment a `<PackageReference Include="FadeBasic.Testing" />` is added.

| Property | Default | What it does |
|---|---|---|
| **`FadeEnableTesting`** | `True` (when FadeBasic.Testing is referenced) | Master switch for the testing integration. Drives `IsTestProject`, `GenerateProgramFile`, the VSTest adapter deployment, FADE0002, and the Build package's `Main`-shape selection. Set to `False` to fully opt out without removing the package reference (e.g., for a published sample that demonstrates the wiring but isn't itself a test target). |
| **`FadeUseMtpRunner`** | `False` | Opt-in to running `dotnet test` through `Microsoft.Testing.Platform` (MTP) instead of classic VSTest. Default is **off** because Rider 2024.x/2025.x does not yet fully support MTP discovery (RIDER-129745). When you set this to `True`, `EnableMicrosoftTestingPlatform`, `UseMicrosoftTestingPlatformRunner`, and `GenerateTestingPlatformEntryPoint` switch on automatically, and `Microsoft.Testing.Platform.MSBuild` is added as a transitive dep. You also need a `global.json` declaring `"test": { "runner": "Microsoft.Testing.Platform" }` next to the csproj — without it, `dotnet test` ignores the property and still uses VSTest. |
| **`IsTestProject`** | `true` (when `FadeEnableTesting=True`) | Required for `dotnet test` to consider the project at all and for IDE Test Explorers to scan its bin. Without it, `dotnet test` silently exits with "Skipping running test for project: To run tests with dotnet test add IsTestProject=true". |
| **`GenerateProgramFile`** | `false` (when `FadeEnableTesting=True`) | Suppresses the parallel `Main` that `Microsoft.NET.Test.Sdk` would otherwise emit. The Fade-generated `GeneratedFade.Main` is the real entry point — a duplicate trips compiler error CS8892. |
| **`EnableMicrosoftTestingPlatform`** | `true` (when `FadeUseMtpRunner=True`) | Tells `dotnet test` "this project is an MTP test app." On .NET 10 SDK the related `Microsoft.Testing.Platform.MSBuild` package's targets hard-error if this is `false` while the package is referenced — that's why we keep MTP fully opt-in and gated. |
| **`UseMicrosoftTestingPlatformRunner`** | `true` (when `FadeUseMtpRunner=True`) | Tells `dotnet test` to invoke our exe via MTP rather than routing through VSTest. |
| **`GenerateTestingPlatformEntryPoint`** | `false` (when `FadeUseMtpRunner=True`) | Same idea as `GenerateProgramFile=false` but for the MTP `Main`. Suppresses `Microsoft.Testing.Platform.MSBuild`'s auto-emitted entry point in favor of the Fade-generated one. |
| **`FadeGenerateNUnitFixture`** | (deprecated) | Old NUnit-fixture-generation path from before the VSTest adapter existed. Setting it now produces FADE0001 telling you to switch to `<FadeEnableTesting>true</FadeEnableTesting>`. Will be removed in a future version. |

## FadeBasic.Testing items

You don't write these — the package contributes them automatically.

| Item | Source | Purpose |
|---|---|---|
| **`<None Include=".../FadeBasic.TestAdapter.dll">`** with `<Link>FadeBasic.TestAdapter.dll</Link>` and `CopyToOutputDirectory=PreserveNewest` | added by FadeBasic.Testing.props | Copies the bundled VSTest adapter DLL from the package's `build/_common/` folder into the consumer's `bin/<TFM>/` root. VSTest's filename-based discovery (`*.TestAdapter.dll`) finds it there; without the `<Link>`, MSBuild would preserve the `_common/` subfolder and the adapter would never be discovered. |
| **`<PackageReference Include="Microsoft.Testing.Platform.MSBuild">`** | added by FadeBasic.Testing.props when `FadeUseMtpRunner=True` | MTP MSBuild integration. Conditional because its targets hard-error on .NET 10 SDK in non-MTP projects. |

`Microsoft.NET.Test.Sdk` is a real nuspec-level dependency of `FadeBasic.Testing` (not a package-props `<PackageReference>`), because runtime assets like `testhost.dll` and `Newtonsoft.Json.dll` only flow through nuspec-declared deps. A package-provided `<PackageReference>` would attach the compile reference but skip the asset deployment, and `dotnet test` would abort with "An assembly specified in the application dependencies manifest (testhost.deps.json) was not found: Newtonsoft.Json".

## Diagnostic codes

Both packages emit MSBuild warnings/errors with stable codes you can grep, suppress, or treat as errors.

| Code | Severity | When it fires | What to do |
|---|---|---|---|
| **FADE0001** | warning | `FadeGenerateNUnitFixture=true` (the deprecated NUnit path) | Replace with `<FadeEnableTesting>true</FadeEnableTesting>`. The new path uses the VSTest adapter, no NUnit dependency required. |
| **FADE0002** | error | `FadeEnableTesting=true` but no `<PackageReference Include="FadeBasic.Testing" />` in the csproj | Add the PackageReference. NuGet doesn't honor PackageReferences added by transitive props files during restore, so this can't be auto-injected. |
| **FADE0003** | warning | MTP runner is on (`FadeUseMtpRunner=true`) but `global.json` exists and doesn't declare the MTP runner | Add `"test": { "runner": "Microsoft.Testing.Platform" }` to your `global.json`. Without it, `dotnet test` will route through VSTest and fail with a Newtonsoft.Json error. |
| **FADE0004** | warning | `global.json` declares the MTP runner but `FadeUseMtpRunner` isn't set | Either delete the `"test": { "runner" }` stanza from `global.json` (default VSTest mode), or set `<FadeUseMtpRunner>true</FadeUseMtpRunner>` (opt back into MTP, accepting the Rider-discovery limitation). Triggered most often when upgrading from an older FadeBasic.Testing version that defaulted to MTP. |

## Choosing between VSTest mode and MTP mode

|  | VSTest (default) | MTP (`FadeUseMtpRunner=true`) |
|---|---|---|
| `dotnet test` from CLI | works | works |
| Visual Studio Test Explorer | works | works (VS 17.13+) |
| VS Code C# Dev Kit | works | partial — uses VSTest discovery regardless |
| JetBrains Rider | works | broken (RIDER-129745) |
| Failure messages | source-linked to `.fbasic` line via the adapter | source-linked to `.fbasic` line via the MTP framework |
| Per-test parallelism | sequential (Fade tests aren't safely parallelizable) | sequential |
| Custom `IFadeTestHost` | yes (resolved by both) | yes (resolved by both) |
| Requires `global.json` | no | yes |

If you have any team members on Rider, stay in VSTest mode. The two paths run the same `FadeTestExecutor` against the same `IFadeTestHost`, so test behavior is identical between them — the only differences are wire-protocol-level. There's no functional reason to prefer MTP today.

## Lifecycle of a build

Knowing the order of operations helps when you're integrating a custom MSBuild target.

1. **Restore.** NuGet downloads `FadeBasic.Build`, `FadeBasic.Lang.Core`, `FadeBasic.Testing` (if referenced), and their transitive deps. Per-package `build/<PackageId>.props` files are auto-imported into your csproj.
2. **Property evaluation.** `FadeBasic.Testing.props` sets `FadeEnableTesting=True` (default-on), which `FadeBasic.Build.targets` later reads. Your csproj's own `<PropertyGroup>` is evaluated *after* package props — so an explicit `<FadeEnableTesting>false</FadeEnableTesting>` in the csproj wins.
3. **`EnsureFadeTestingReferenced`** target (Build package) runs and fires FADE0002 if `FadeEnableTesting=True` without a Testing PackageReference.
4. **`WarnDeprecatedFadeFlags`** + **`WarnStaleMtpGlobalJson`** + **`EnsureFadeTestingGlobalJson`** targets (Testing package) run, surfacing FADE0001/FADE0003/FADE0004 as appropriate.
5. **`GenerateFadeBasic`** target (Build package, before `CoreCompile`) runs `FadeProjectTask`. Inputs: `@(FadeSource)` (your `.fbasic` files), `@(FadeCommand)` (your command classes), `@(ReferencePath)` (resolved DLLs). Output: a generated `.g.cs` file containing the bytecode-bearing launchable class.
6. **`CoreCompile`** picks up the generated file and compiles your project as usual.
7. **Bin contents.** `FadeBasic.TestAdapter.dll`, `FadeBasic.Testing.dll`, `Microsoft.Testing.Platform.dll`, `testhost.dll`, `Newtonsoft.Json.dll`, plus the standard build outputs.
8. **`GenerateFadeDocs`** target (after `ResolveReferences`) writes `FadeCommandDocs.md` to `$(IntermediateOutputPath)`.

## Common patterns

### Adding a custom `IFadeTestHost`

Drop a class implementing `FadeBasic.Testing.IFadeTestHost` anywhere in your project, tag it `[FadeTestHost]`, and the runner discovers it automatically. No csproj edits needed. Useful for projects (like a MonoGame app) that need to reset host-side state between tests.

```csharp
[FadeBasic.Testing.FadeTestHost]
public sealed class MyHost : FadeBasic.Testing.IFadeTestHost
{
    public Task InitializeAsync(...) { ... }   // once per process
    public Task BeforeAllTestsAsync(...) { ... }  // once per `dotnet test`
    public Task<FadeTestResult> RunTestAsync(...) { ... }  // per test
    public Task AfterAllTestsAsync(...) { ... }
    public ValueTask DisposeAsync() => default;
}
```

### Custom `Main`

If your project ships its own entry point (e.g., for a graphics host), set `<FadeGenerateMain>False</FadeGenerateMain>` and call into the generated launchable class manually. The class is still named `<FadeGeneratedLaunchType>` (default `GeneratedFade`) and exposes static helpers for both the run-program and run-tests dispatch shapes.

### Multi-`.fbasic` projects

Add multiple `<FadeSource>` items in source order. The compiler concatenates them into one virtual source unit but preserves a `SourceMap` so error messages and test `sourceFilePath`s stay file-accurate. Each `test ... endtest` block in your manifest carries the originating `.fbasic` path, which IDE Test Explorers use to source-link tests in the tree.

### Opt out of testing for a sample

```xml
<PropertyGroup>
  <FadeEnableTesting>false</FadeEnableTesting>
</PropertyGroup>
<ItemGroup>
  <PackageReference Include="FadeBasic.Testing" Version="..." />
</ItemGroup>
```

Useful for documentation samples that show the *shape* of a Fade project including the Testing reference but shouldn't actually appear in Test Explorer.

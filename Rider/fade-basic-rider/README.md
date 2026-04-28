# Fade Basic — JetBrains Rider plugin

This folder contains a **Rider / IntelliJ Platform** plugin that wires your existing **Fade Basic LSP** (`FadeBasic/LSP`) and **DAP** (`FadeBasic/DAP`) into Rider the same way the VS Code extension does: stdio for the language server, and `dotnet …/DAP.dll` with the `FADE_*` environment variables for the debug adapter.

## Requirements

- **JDK 17+** for Gradle (the IntelliJ Platform Gradle Plugin 2.x and Gradle 9 require it). Examples:
  - macOS: `export JAVA_HOME="/Applications/IntelliJ IDEA.app/Contents/jbr/Contents/Home"`
  - Or install a JDK 17+ distribution and point `JAVA_HOME` at it.
- **Gradle** is invoked via the included wrapper (`./gradlew`). No separate Gradle install is required.
- **.NET SDK** on the machine where Rider runs (to execute `LSP.dll` / `DAP.dll` or `dotnet run --project …`).

## First-time configuration (in Rider)

After installing the plugin (or loading it from Gradle), open **Settings → Tools → Fade Basic** and set:

| Setting | Purpose |
|--------|---------|
| **Dotnet executable** | Same as VS Code `conf.language.fade.dotnetPath` (default `dotnet`). |
| **Path to LSP.dll** | Published layout, e.g. `FadeBasic/LSP/bin/Debug/net8.0/LSP.dll`. Leave empty if you use the project path. |
| **Path to LSP.csproj** | Dev layout: `FadeBasic/LSP/LSP.csproj`. Command: `dotnet run --project "<path>" --` (stdio). |
| **Path to DAP.dll** | e.g. `FadeBasic/DAP/bin/Debug/net8.0/DAP.dll`. Required for the **Fade Basic (DAP)** run configuration. |

You need **either** LSP.dll **or** LSP.csproj for the language server to start when you open a `.fbasic` / `.fb` file.

## Local development

From this directory:

```bash
export JAVA_HOME="<path-to-jdk-17+>"
./gradlew runIde
```

This downloads the Rider platform SDK (multi-OS archive, `useInstaller = false`) on first run, then starts a **sandbox Rider** with the plugin loaded.

Other useful tasks:

| Task | Description |
|------|-------------|
| `./gradlew test` | Unit tests (launch/env helpers; no Rider UI). |
| `./gradlew buildPlugin` | Produces `build/distributions/*.zip` for manual install (**Settings → Plugins → ⚙ → Install Plugin from Disk…**). |
| `./gradlew compileKotlin` | Fast compile check. |

## Language server (LSP)

- Implemented with JetBrains’ **`LspServerSupportProvider`** (`com.intellij.modules.lsp`). Opening a **Fade Basic** file starts one project-wide server using **stdio** (no named pipe).
- Your `FadeBasic/LSP` entrypoint already uses stdin/stdout when no `--pipe=` argument is present.

## Debug adapter (DAP)

- A run configuration type **Fade Basic (DAP)** launches:

  `dotnet <path-to-DAP.dll>`

  with environment variables aligned with `VsCode/basicscript/src/extension.ts` (`FADE_PROGRAM`, `FADE_WAIT_FOR_DEBUG`, `FADE_DOTNET_PATH`, optional `FADE_DEBUGGER_LOG_PATH`, `FADE_DAP_LOG_PATH`).

- **Run** or **Debug** from the toolbar runs that process and attaches the **Run** tool window to its stdout/stderr. Full breakpoint/step UI would require a deeper `XDebugProcess` bridge to DAP; this plugin intentionally starts the same adapter process VS Code uses so you can validate wiring and logs in Rider.

## Rider platform note

`build.gradle.kts` uses:

```kotlin
rider("2024.3.5") {
    useInstaller.set(false)
}
jetbrainsRuntime()
```

Rider is resolved as a **multi-OS archive** (not the full installer); JetBrains Runtime is added explicitly so `runIde` and tests can start the IDE.

## Project layout

```
fade-basic-rider/
  build.gradle.kts
  settings.gradle.kts
  src/main/kotlin/ink/brewed/fadebasic/rider/   # LSP provider, file type, DAP run config, settings
  src/main/resources/META-INF/plugin.xml
  src/test/kotlin/…/FadeBasicLaunchSpecsTest.kt
  README.md
```

## Compatibility

- `plugin.xml` declares `since-build="243"` (2024.3) through `253.*`, LSP module (`com.intellij.modules.lsp`), and Rider (`com.intellij.modules.rider`).

## Publishing to JetBrains Marketplace

The plugin is published alongside the NuGet packages and VS Code extension via the GitHub Actions release workflow.

### One-time setup

1. **Create a JetBrains Marketplace account** at [plugins.jetbrains.com](https://plugins.jetbrains.com).

2. **Register the plugin** by uploading the first build manually. Build it locally:
   ```bash
   export JAVA_HOME="<path-to-jdk-17+>"
   ./gradlew buildPlugin -PpluginVersion=0.0.1
   ```
   Then upload `build/distributions/fade-basic-rider-0.0.1.zip` via the marketplace UI. This registers the plugin ID `ink.brewed.fadebasic`.

3. **Generate a marketplace token** — go to your JetBrains Hub profile → Authentication → New token. Grant the **Plugin Repository** scope.

4. **Generate a signing key pair.** JetBrains Marketplace requires every plugin upload to be
   signed with a developer-owned RSA key pair. Signing isn't authentication — your marketplace
   token already proves *who* uploaded — it's a tamper-evidence seal: each subsequent version
   must be signed by the same key as the first one, so nobody who later steals your token (or
   compromises some intermediate mirror) can push a hostile update under your plugin ID. The
   IDE refuses to install a signed plugin whose signature doesn't match the previous version's,
   which means a stolen token alone can't ship malware to your users. The cert is self-signed
   and never published — only the resulting signature is verified — so no CA / trust-store
   setup is involved. Generate the pair once, store it as a secret, reuse it for every release:

   ```bash
   openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:4096 -out rider-private.pem
   openssl req -new -x509 -key rider-private.pem -out rider-cert-chain.crt -days 3650 -subj "/CN=Brewed Ink"
   ```
   
   Keep `rider-private.pem` somewhere durable (password manager, encrypted backup). If you lose
   it, you can't ship updates to this plugin ID — you'd have to publish a new plugin with a new
   ID and ask users to migrate.

5. **Add GitHub Secrets** to the repository:

   | Secret | Value |
   |--------|-------|
   | `JETBRAINS_MARKETPLACE_TOKEN` | The token from step 3 |
   | `JETBRAINS_PLUGIN_SIGNING_KEY` | Contents of `rider-private.pem` |
   | `JETBRAINS_PLUGIN_SIGNING_CERT_CHAIN` | Contents of `rider-cert-chain.crt` |
   | `JETBRAINS_PLUGIN_SIGNING_KEY_PASSPHRASE` | Passphrase for the key (empty string if none) |

### How it works in CI

The release workflow (`release.yml`) has a `publishRider` toggle. When enabled:

1. Sets up JDK 17 via `actions/setup-java`
2. Writes the signing cert/key from secrets to temp files
3. Runs `./gradlew buildPlugin publishPlugin -PpluginVersion=X.Y.Z`
4. Attaches the plugin zip to the GitHub Release as a download

The version is passed from the workflow inputs so it stays in sync with the NuGet and VS Code versions.

### Local builds

To build a distributable zip without publishing:
```bash
export JAVA_HOME="<path-to-jdk-17+>"
./gradlew buildPlugin -PpluginVersion=0.0.63
# output: build/distributions/fade-basic-rider-0.0.63.zip
```

Install manually in Rider via **Settings → Plugins → ⚙ → Install Plugin from Disk…**.

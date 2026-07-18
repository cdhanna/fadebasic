import java.io.File
import org.jetbrains.kotlin.gradle.dsl.JvmTarget
import org.jetbrains.kotlin.gradle.tasks.KotlinCompile
import org.gradle.api.tasks.Copy

plugins {
    kotlin("jvm") version "2.1.0"
    id("org.jetbrains.intellij.platform") version "2.15.0"
}

group = "ink.brewed.fadebasic"
version = project.findProperty("pluginVersion")?.toString() ?: "0.1.0-SNAPSHOT"

repositories {
    mavenCentral()
    intellijPlatform {
        defaultRepositories()
    }
}

dependencies {
    intellijPlatform {
        rider("2025.2.1") {
            useInstaller.set(false)
        }
        jetbrainsRuntime()
        zipSigner()
    }

    testImplementation("org.junit.jupiter:junit-jupiter:5.11.4")
    testRuntimeOnly("org.junit.platform:junit-platform-launcher")
}

// Toolchain language version is overridable via -PfadeJdkVersion=21 so contributors
// who only have JDK 21 (e.g. Rider's bundled JBR) can compile without installing
// JDK 17 explicitly. The compiled bytecode level (jvmTarget below) stays at JVM 17
// regardless, keeping the plugin runnable on the IntelliJ Platform's minimum runtime.
val fadeJdkVersion: String = (project.findProperty("fadeJdkVersion") as String?) ?: "17"

// Release switch. Passed as `-PfadeRelease=true` by the publish CI
// (.github/workflows/release.yml). It flips the plugin from "dev, runs the
// LSP/DAP straight from ../../FadeBasic source" to "shipping artifact, runs
// the bundled DLLs" — same distinction the VS Code extension makes:
//   - dev (default): bake the absolute csproj paths into FadeBasicDevPaths so
//     `dotnet run --project …` drives the LSP/DAP from source (fast iteration).
//   - release: bake EMPTY dev paths (so a build-machine path like
//     /home/runner/work/... can never leak into the shipped plugin and get
//     `dotnet run` against a csproj that doesn't exist on the user's box), and
//     REQUIRE the bundled LSP.dll/DAP.dll to be present in the zip.
val fadeRelease: Boolean = (project.findProperty("fadeRelease") as String?)?.toBoolean() ?: false

kotlin {
    jvmToolchain(fadeJdkVersion.toInt())
    sourceSets.named("main") {
        kotlin.srcDir(layout.buildDirectory.dir("generated/kotlin/main"))
    }
}

fun escapeKotlinString(s: String): String = buildString {
    for (ch in s) {
        when (ch) {
            '\\' -> append("\\\\")
            '"' -> append("\\\"")
            '$' -> append("\\$")
            else -> append(ch)
        }
    }
}

val generateFadeDevPaths by tasks.registering {
    group = "fade"
    description =
        "Writes FadeBasicDevPaths from ../../FadeBasic relative to Rider/fade-basic-rider (no dependency on the opened solution)."
    val outDir = layout.buildDirectory.dir("generated/kotlin/main")
    val lspCsproj = layout.projectDirectory.file("../../FadeBasic/LSP/LSP.csproj").asFile.absoluteFile.normalize()
    val dapCsproj = layout.projectDirectory.file("../../FadeBasic/DAP/DAP.csproj").asFile.absoluteFile.normalize()
    // Release bakes empty paths so the dev csproj mapping never ships; dev
    // bakes the real absolute paths. Declared as an input so toggling the
    // flag correctly invalidates the task's up-to-date state.
    val bakeDevPaths = !fadeRelease
    inputs.property("bakeDevPaths", bakeDevPaths)
    outputs.dir(outDir)
    doLast {
        val dir = outDir.get().asFile.apply { mkdirs() }
        val lspEsc = escapeKotlinString(if (bakeDevPaths) lspCsproj.absolutePath else "")
        val dapEsc = escapeKotlinString(if (bakeDevPaths) dapCsproj.absolutePath else "")
        File(dir, "FadeBasicDevPaths.generated.kt").writeText(
            """
            package ink.brewed.fadebasic.rider

            object FadeBasicDevPaths {
                const val LSP_PROJECT: String = "$lspEsc"
                const val DAP_PROJECT: String = "$dapEsc"
            }
            """.trimIndent() + "\n",
        )
    }
}

// Match Kotlin's jvmTarget to the toolchain language version so JVM-target
// compatibility validation accepts the build (Kotlin and Java tasks must agree).
// Default is 17 to match the platform; opt-up to 21 with -PfadeJdkVersion=21 when
// JDK 17 isn't installed.
val fadeJvmTarget: JvmTarget = when (fadeJdkVersion) {
    "21" -> JvmTarget.JVM_21
    "20" -> JvmTarget.JVM_20
    "19" -> JvmTarget.JVM_19
    "18" -> JvmTarget.JVM_18
    else -> JvmTarget.JVM_17
}

tasks.withType<KotlinCompile>().configureEach {
    dependsOn(generateFadeDevPaths)
    compilerOptions {
        jvmTarget.set(fadeJvmTarget)
        freeCompilerArgs.add("-Xjvm-default=all")
    }
}

tasks.withType<JavaCompile>().configureEach {
    sourceCompatibility = fadeJdkVersion
    targetCompatibility = fadeJdkVersion
}

tasks.test {
    useJUnitPlatform()
}

val fadeBasicRoot = layout.projectDirectory.asFile.toPath().normalize().resolve("../../FadeBasic").normalize()
// Locate a built tool DLL. Probes, newest-first, every place the .NET build
// might have dropped it: an explicit -PfadeToolsDir override, then the Release
// and Debug net8.0 outputs. CI's install.sh builds the solution in Release
// (→ bin/Release/net8.0), while a local `dotnet build` defaults to Debug
// (→ bin/Debug/net8.0) — the old code only checked Debug, so CI shipped no
// bundled tools at all. Returns null when the DLL hasn't been built anywhere.
val fadeToolsOverrideDir: java.nio.file.Path? =
    (project.findProperty("fadeToolsDir") as String?)?.let { file(it).toPath().normalize() }
fun resolveFadeToolDll(projectName: String, dllName: String): File? {
    val candidates = buildList {
        fadeToolsOverrideDir?.let { add(it.resolve(dllName)) }
        add(fadeBasicRoot.resolve("$projectName/bin/Release/net8.0/$dllName"))
        add(fadeBasicRoot.resolve("$projectName/bin/Debug/net8.0/$dllName"))
    }.map { it.toFile() }.filter { it.isFile }
    return candidates.maxByOrNull { it.lastModified() }
}
val copyFadeBundledTools = tasks.register<Copy>("copyFadeBundledTools") {
    group = "fade"
    description =
        "Bundle LSP.dll and DAP.dll into the plugin, probing Release then Debug net8.0 output " +
            "(override with -PfadeToolsDir). Re-run Gradle after building the .NET solution."
    val lspDll = resolveFadeToolDll("LSP", "LSP.dll")
    val dapDll = resolveFadeToolDll("DAP", "DAP.dll")
    // A release MUST ship both DLLs — once the dev csproj mapping is baked
    // empty they're the only tool source, so keep the task enabled even when
    // they're missing and fail at execution rather than silently publishing a
    // plugin that dies at runtime with "set paths in Settings".
    enabled = fadeRelease || lspDll != null || dapDll != null
    doFirst {
        if (fadeRelease && (lspDll == null || dapDll == null)) {
            throw GradleException(
                "fadeRelease build is missing bundled tools (LSP.dll=${lspDll != null}, DAP.dll=${dapDll != null}). " +
                    "Build the .NET solution first (e.g. `dotnet build FadeBasic/build.sln -c Release`) or pass -PfadeToolsDir.",
            )
        }
    }
    into(layout.buildDirectory.dir("fade-tools-resources"))
    lspDll?.let { from(it) }
    dapDll?.let { from(it) }
}

tasks.named<ProcessResources>("processResources") {
    dependsOn(copyFadeBundledTools)
    doFirst {
        layout.buildDirectory.dir("fade-tools-resources").get().asFile.mkdirs()
    }
    from(layout.buildDirectory.dir("fade-tools-resources")) {
        into("tools")
    }
}

intellijPlatform {
    pluginConfiguration {
        id = "ink.brewed.fadebasic"
        name = "Fade Basic"
        version = project.version.toString()
        description = "Fade Basic language support via LSP and DAP launch integration for Rider."
        vendor {
            name = "Brewed Ink"
            url = "https://brewed.ink"
        }
        ideaVersion {
            sinceBuild = "252"
            untilBuild = "262.*"
        }
    }
    signing {
        val certFile = providers.environmentVariable("JETBRAINS_PLUGIN_SIGNING_CERT_CHAIN_FILE")
        val keyFile = providers.environmentVariable("JETBRAINS_PLUGIN_SIGNING_KEY_FILE")
        if (certFile.isPresent) certificateChainFile.set(file(certFile.get()))
        if (keyFile.isPresent) privateKeyFile.set(file(keyFile.get()))
        password.set(providers.environmentVariable("JETBRAINS_PLUGIN_SIGNING_KEY_PASSPHRASE"))
    }
    publishing {
        token.set(providers.environmentVariable("JETBRAINS_MARKETPLACE_TOKEN"))
    }
}

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

kotlin {
    jvmToolchain(17)
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
    outputs.dir(outDir)
    doLast {
        val dir = outDir.get().asFile.apply { mkdirs() }
        val lspEsc = escapeKotlinString(lspCsproj.absolutePath)
        val dapEsc = escapeKotlinString(dapCsproj.absolutePath)
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

tasks.withType<KotlinCompile>().configureEach {
    dependsOn(generateFadeDevPaths)
    compilerOptions {
        jvmTarget.set(JvmTarget.JVM_17)
        freeCompilerArgs.add("-Xjvm-default=all")
    }
}

tasks.test {
    useJUnitPlatform()
}

val fadeBasicRoot = layout.projectDirectory.asFile.toPath().normalize().resolve("../../FadeBasic").normalize()
val copyFadeBundledTools = tasks.register<Copy>("copyFadeBundledTools") {
    group = "fade"
    description =
        "Copy LSP.dll and DAP.dll from ../../FadeBasic when those files exist (re-run Gradle after `dotnet build`)."
    val lspDll = fadeBasicRoot.resolve("LSP/bin/Debug/net8.0/LSP.dll").toFile()
    val dapDll = fadeBasicRoot.resolve("DAP/bin/Debug/net8.0/DAP.dll").toFile()
    enabled = lspDll.exists() || dapDll.exists()
    into(layout.buildDirectory.dir("fade-tools-resources"))
    if (lspDll.exists()) from(lspDll)
    if (dapDll.exists()) from(dapDll)
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

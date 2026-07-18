package ink.brewed.fadebasic.rider

import ink.brewed.fadebasic.rider.settings.FadeBasicRiderSettings
import java.io.File

object FadeBasicToolingPaths {

    // The baked dev csproj path is a developer convenience (run the LSP/DAP
    // from ../../FadeBasic source). Release builds bake it empty (see
    // build.gradle.kts `fadeRelease`), but guard on existence anyway: a path
    // like /home/runner/work/.../LSP.csproj that leaks from a build machine
    // must never be handed to `dotnet run --project` on a user's box — skip it
    // and fall through to the bundled DLL instead of crashing.
    private fun String.existsAsFile(): Boolean = isNotEmpty() && File(this).isFile

    fun canStartLsp(state: FadeBasicRiderSettings.State, classLoader: ClassLoader?): Boolean =
        runCatching { lspArgv(state, classLoader) }.isSuccess

    fun lspArgv(state: FadeBasicRiderSettings.State, classLoader: ClassLoader?): List<String> {
        val userDll = state.lspDllPath.trim()
        if (userDll.isNotEmpty()) return withOptionalLspLog(listOf(userDll), state)

        val userProj = state.lspProjectPath.trim()
        if (userProj.isNotEmpty()) return withOptionalLspLog(listOf("run", "--project", userProj, "--"), state)

        val baked = FadeBasicDevPaths.LSP_PROJECT.trim()
        if (baked.existsAsFile()) return withOptionalLspLog(listOf("run", "--project", baked, "--"), state)

        val bundled = classLoader?.let { FadeBasicBundledToolCache.bundledDllPath(it, "LSP.dll") }
        if (bundled != null) return withOptionalLspLog(listOf(bundled.toString()), state)

        error(
            "Fade Basic LSP: set paths in Settings | Fade Basic, or rebuild the plugin so Gradle can bake " +
                "FadeBasicDevPaths from ../../FadeBasic (see README).",
        )
    }

    private fun withOptionalLspLog(argv: List<String>, state: FadeBasicRiderSettings.State): List<String> =
        if (!state.lspWriteLogFile) argv else argv + listOf("--use-log-path")

    fun canStartDap(state: FadeBasicRiderSettings.State, classLoader: ClassLoader?): Boolean =
        runCatching { dapArgv(state, classLoader) }.isSuccess

    fun dapArgv(state: FadeBasicRiderSettings.State, classLoader: ClassLoader?): List<String> {
        val userDll = state.dapDllPath.trim()
        if (userDll.isNotEmpty()) return listOf(userDll)

        val userProj = state.dapProjectPath.trim()
        if (userProj.isNotEmpty()) return listOf("run", "--project", userProj, "--")

        val baked = FadeBasicDevPaths.DAP_PROJECT.trim()
        if (baked.existsAsFile()) return listOf("run", "--project", baked, "--")

        val bundled = classLoader?.let { FadeBasicBundledToolCache.bundledDllPath(it, "DAP.dll") }
        if (bundled != null) return listOf(bundled.toString())

        error(
            "Fade Basic DAP: set paths in Settings | Fade Basic, or rebuild the plugin so Gradle can bake " +
                "FadeBasicDevPaths from ../../FadeBasic (see README).",
        )
    }
}

package ink.brewed.fadebasic.rider

import ink.brewed.fadebasic.rider.settings.FadeBasicRiderSettings
import java.nio.file.Path

object FadeBasicLaunchSpecs {

    fun canStartLsp(settings: FadeBasicRiderSettings.State): Boolean =
        FadeBasicToolingPaths.canStartLsp(settings, null)

    fun lspArgv(settings: FadeBasicRiderSettings.State): List<String> =
        FadeBasicToolingPaths.lspArgv(settings, null)

    fun dapArgv(settings: FadeBasicRiderSettings.State): List<String> =
        FadeBasicToolingPaths.dapArgv(settings, null)

    fun dapEnvironment(
        settings: FadeBasicRiderSettings.State,
        programCsproj: String,
        waitForDebugger: Boolean,
        debuggerLogPath: String,
        dapLogPath: String,
    ): Map<String, String> {
        val env = LinkedHashMap<String, String>()
        env["FADE_PROGRAM"] = programCsproj
        env["FADE_WAIT_FOR_DEBUG"] = if (waitForDebugger) "true" else "false"
        env["FADE_DOTNET_PATH"] = settings.dotnetPath.ifBlank { "dotnet" }
        val dbg = debuggerLogPath.trim()
        if (dbg.isNotEmpty()) {
            env["FADE_DEBUGGER_LOG_PATH"] = dbg
        }
        val dap = dapLogPath.trim()
        if (dap.isNotEmpty()) {
            env["FADE_DAP_LOG_PATH"] = dap
        }
        return env
    }

    fun workingDirectoryOrNull(path: String?): Path? {
        val t = path?.trim().orEmpty()
        if (t.isEmpty()) return null
        return Path.of(t)
    }
}

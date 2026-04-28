package ink.brewed.fadebasic.rider

import ink.brewed.fadebasic.rider.settings.FadeBasicRiderSettings
import org.junit.jupiter.api.Assertions.assertEquals
import org.junit.jupiter.api.Assertions.assertFalse
import org.junit.jupiter.api.Assertions.assertTrue
import org.junit.jupiter.api.Test

class FadeBasicLaunchSpecsTest {

    @Test
    fun canStartLsp_whenDllConfigured() {
        val s = FadeBasicRiderSettings.State(lspDllPath = "/x/LSP.dll")
        assertTrue(FadeBasicLaunchSpecs.canStartLsp(s))
    }

    @Test
    fun canStartLsp_whenProjectConfigured() {
        val s = FadeBasicRiderSettings.State(lspProjectPath = "/repo/LSP/LSP.csproj")
        assertTrue(FadeBasicLaunchSpecs.canStartLsp(s))
    }

    @Test
    fun canStartLsp_trueWhenUnsetUsesGradleBakedPaths() {
        val s = FadeBasicRiderSettings.State()
        assertTrue(FadeBasicLaunchSpecs.canStartLsp(s))
    }

    @Test
    fun lspArgv_prefersDllOverProject() {
        val s =
            FadeBasicRiderSettings.State(
                lspDllPath = "/a/LSP.dll",
                lspProjectPath = "/b/LSP.csproj",
                lspWriteLogFile = false,
            )
        assertEquals(listOf("/a/LSP.dll"), FadeBasicLaunchSpecs.lspArgv(s))
    }

    @Test
    fun lspArgv_usesProjectWhenDllEmpty() {
        val s =
            FadeBasicRiderSettings.State(
                lspProjectPath = "/b/LSP.csproj",
                lspWriteLogFile = false,
            )
        assertEquals(listOf("run", "--project", "/b/LSP.csproj", "--"), FadeBasicLaunchSpecs.lspArgv(s))
    }

    @Test
    fun lspArgv_whenUnset_usesBakedCsprojWithDotnetRunAndLogFlag() {
        val s = FadeBasicRiderSettings.State(lspWriteLogFile = true)
        assertEquals(
            listOf("run", "--project", FadeBasicDevPaths.LSP_PROJECT, "--", "--use-log-path"),
            FadeBasicLaunchSpecs.lspArgv(s),
        )
    }

    @Test
    fun lspArgv_whenLogDisabled_omitsUseLogPath() {
        val s = FadeBasicRiderSettings.State(lspWriteLogFile = false)
        assertEquals(
            listOf("run", "--project", FadeBasicDevPaths.LSP_PROJECT, "--"),
            FadeBasicLaunchSpecs.lspArgv(s),
        )
    }

    @Test
    fun dapArgv_whenUnset_usesBakedCsprojWithDotnetRun() {
        val s = FadeBasicRiderSettings.State()
        assertEquals(
            listOf("run", "--project", FadeBasicDevPaths.DAP_PROJECT, "--"),
            FadeBasicLaunchSpecs.dapArgv(s),
        )
    }

    @Test
    fun dapArgv_usesProjectFromSettings() {
        val s = FadeBasicRiderSettings.State(dapProjectPath = "/repo/DAP/DAP.csproj")
        assertEquals(listOf("run", "--project", "/repo/DAP/DAP.csproj", "--"), FadeBasicLaunchSpecs.dapArgv(s))
    }

    @Test
    fun dapEnvironment_matchesVsCodeExtensionShape() {
        val s = FadeBasicRiderSettings.State(dotnetPath = "/usr/bin/dotnet")
        val env =
            FadeBasicLaunchSpecs.dapEnvironment(
                settings = s,
                programCsproj = "/proj/App.csproj",
                waitForDebugger = true,
                debuggerLogPath = "/tmp/dbg.log",
                dapLogPath = "/tmp/dap.log",
            )
        assertEquals("/proj/App.csproj", env["FADE_PROGRAM"])
        assertEquals("true", env["FADE_WAIT_FOR_DEBUG"])
        assertEquals("/usr/bin/dotnet", env["FADE_DOTNET_PATH"])
        assertEquals("/tmp/dbg.log", env["FADE_DEBUGGER_LOG_PATH"])
        assertEquals("/tmp/dap.log", env["FADE_DAP_LOG_PATH"])
    }

    @Test
    fun dapEnvironment_omitsOptionalLogsWhenBlank() {
        val s = FadeBasicRiderSettings.State(dotnetPath = "dotnet")
        val env =
            FadeBasicLaunchSpecs.dapEnvironment(
                settings = s,
                programCsproj = "C.csproj",
                waitForDebugger = false,
                debuggerLogPath = "   ",
                dapLogPath = "",
            )
        assertEquals("false", env["FADE_WAIT_FOR_DEBUG"])
        assertFalse(env.containsKey("FADE_DEBUGGER_LOG_PATH"))
        assertFalse(env.containsKey("FADE_DAP_LOG_PATH"))
    }
}

package ink.brewed.fadebasic.rider.run

import com.intellij.execution.configurations.CommandLineState
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.process.KillableColoredProcessHandler
import com.intellij.execution.process.ProcessHandler
import com.intellij.execution.runners.ExecutionEnvironment
import ink.brewed.fadebasic.rider.settings.FadeBasicRiderSettings
import java.io.File
import java.nio.file.Paths

/**
 * **Run** (not **Debug**): runs the user's Fade Basic host app with `dotnet run --project` and the configured `.csproj`.
 *
 * The DAP adapter is not used here: the adapter only learns `program` from a DAP `launch` request on stdio,
 * so starting DAP with `FADE_PROGRAM` alone would not run the app.
 */
class FadeBasicDapNonDebugRunProfileState(
    environment: ExecutionEnvironment,
    private val configuration: FadeBasicDapRunConfiguration,
) : CommandLineState(environment) {

    override fun startProcess(): ProcessHandler {
        val st = FadeBasicRiderSettings.getInstance().state
        val dotnet = st.dotnetPath.ifBlank { "dotnet" }
        val csproj = Paths.get(configuration.resolvedProgramPath()).toAbsolutePath().normalize().toString()
        val line = GeneralCommandLine().withExePath(dotnet).withParameters("run", "--project", csproj, "--")
        line.withParentEnvironmentType(GeneralCommandLine.ParentEnvironmentType.CONSOLE)
        // Ensure we do not inherit a debug session from the IDE / shell.
        line.environment["FADE_BASIC_DEBUG"] = "false"
        val base = configuration.project.basePath
        if (!base.isNullOrBlank()) {
            line.workDirectory = File(base)
        }
        return KillableColoredProcessHandler(line)
    }
}

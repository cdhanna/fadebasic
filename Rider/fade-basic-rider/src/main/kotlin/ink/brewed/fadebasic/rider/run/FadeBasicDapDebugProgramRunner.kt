package ink.brewed.fadebasic.rider.run

import com.intellij.execution.ExecutionException
import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.configurations.RunProfile
import com.intellij.execution.configurations.RunProfileState
import com.intellij.execution.configurations.RunnerSettings
import com.intellij.execution.executors.DefaultDebugExecutor
import com.intellij.execution.process.ProcessHandler
import com.intellij.execution.runners.ExecutionEnvironment
import com.intellij.execution.runners.GenericProgramRunner
import com.intellij.execution.ui.RunContentDescriptor
import com.intellij.openapi.project.Project
import com.intellij.xdebugger.XDebugProcessStarter
import com.intellij.xdebugger.XDebugSession
import com.intellij.xdebugger.XDebuggerManager
import ink.brewed.fadebasic.rider.FadeBasicLaunchSpecs
import ink.brewed.fadebasic.rider.FadeBasicToolingPaths
import ink.brewed.fadebasic.rider.settings.FadeBasicRiderSettings
import java.io.File
import java.io.OutputStream

class FadeBasicDapDebugProgramRunner : GenericProgramRunner<RunnerSettings>() {

    override fun getRunnerId(): String = "FadeBasicDapDebugProgramRunner"

    override fun canRun(executorId: String, profile: RunProfile): Boolean =
        executorId == DefaultDebugExecutor.EXECUTOR_ID && profile is FadeBasicDapRunConfiguration

    override fun doExecute(
        project: Project,
        state: RunProfileState,
        contentToReuse: RunContentDescriptor?,
        environment: ExecutionEnvironment,
    ): RunContentDescriptor? {
        val configuration = environment.runProfile as FadeBasicDapRunConfiguration
        val st = FadeBasicRiderSettings.getInstance().state
        val cl = FadeBasicDapRunConfiguration::class.java.classLoader
        if (!FadeBasicToolingPaths.canStartDap(st, cl)) {
            throw ExecutionException(
                "Fade Basic DAP is not configured: set DAP paths under Settings | Fade Basic, " +
                    "or rebuild the plugin so Gradle can bake FadeBasicDevPaths / tools (see README).",
            )
        }
        val dotnet = st.dotnetPath.ifBlank { "dotnet" }
        val argv = FadeBasicToolingPaths.dapArgv(st, cl)
        val sessionLogs = FadeBasicDapSessionLogPaths.resolve(st, configuration)
        val line = GeneralCommandLine().withExePath(dotnet).withParameters(argv)
        line.withParentEnvironmentType(GeneralCommandLine.ParentEnvironmentType.CONSOLE)
        line.environment.putAll(
            FadeBasicLaunchSpecs.dapEnvironment(
                st,
                configuration.resolvedProgramPath(),
                configuration.waitForDebugger,
                sessionLogs.debuggerLogPath,
                sessionLogs.dapLogPath,
            ),
        )
        val base = configuration.project.basePath
        if (!base.isNullOrBlank()) {
            line.workDirectory = File(base)
        }

        // Redirect adapter stderr to a file rather than relying on the pipe — when .NET
        // aborts on an unhandled exception (SIGABRT, exit 134), it doesn't reliably flush
        // the stderr pipe before death, so a piped reader truncates after the first line.
        // Writing straight to a file lets the kernel flush after the process exits and
        // gives us the full stack trace for diagnosis.
        val stderrFile = stderrFileFor(sessionLogs)
        val process = line.toProcessBuilder()
            .redirectErrorStream(false)
            .redirectError(stderrFile)
            .start()
        val handler = DapProcessHandler(process)
        val log = com.intellij.openapi.diagnostic.logger<FadeBasicDapDebugProgramRunner>()
        // Monitor the process so the handler fires processTerminated when DAP exits on its own.
        // Logged so we can correlate IDE-side "Disconnected" UI with the actual adapter exit.
        com.intellij.util.concurrency.AppExecutorUtil.getAppExecutorService().execute {
            try {
                val exitCode = process.waitFor()
                log.warn("Fade DAP adapter process exited with code=$exitCode (PID=${process.pid()})")
                // Drain the stderr file we redirected to. Limit how much we copy into the
                // IDE log to avoid spamming, but show enough to capture a typical .NET
                // unhandled-exception report.
                runCatching {
                    if (stderrFile.exists() && stderrFile.length() > 0) {
                        val text = stderrFile.readText().take(16_000)
                        if (text.isNotBlank()) {
                            log.warn("Fade DAP adapter stderr:\n$text")
                        }
                    }
                }
                if (exitCode != 0) {
                    val msg = if (configuration.mode == FadeBasicDapRunConfiguration.Mode.ATTACH) {
                        "Fade DAP adapter crashed (exit=$exitCode). The attached game may be stuck " +
                            "at a breakpoint with no debugger to resume it; restart the game to recover. " +
                            "See idea.log for stderr."
                    } else {
                        "Fade DAP adapter crashed (exit=$exitCode). See idea.log for stderr."
                    }
                    com.intellij.notification.Notifications.Bus.notify(
                        com.intellij.notification.Notification(
                            "Fade Basic",
                            "Fade DAP adapter exited unexpectedly",
                            msg,
                            com.intellij.notification.NotificationType.ERROR,
                        ),
                        project,
                    )
                }
                handler.fireTerminated(exitCode)
            } catch (e: Exception) {
                log.warn("Fade DAP adapter waitFor failed", e)
            }
        }

        // Start the debug session -- this creates the Debug tool window tab
        val session = XDebuggerManager.getInstance(project).startSession(
            environment,
            object : XDebugProcessStarter() {
                override fun start(session: XDebugSession) =
                    FadeBasicDapDebugProcess(session, handler, configuration, sessionLogs)
            },
        )
        handler.startNotify()

        // Return the descriptor from the debug session so the framework shows the Debug tab
        return session.runContentDescriptor
    }

    /** Pick a path to redirect adapter stderr into for this session. We co-locate it with the
     *  DAP/debugger logs when those are configured (so all session diagnostics live in one
     *  folder); otherwise fall back to a temp file. The file is overwritten on each session. */
    private fun stderrFileFor(sessionLogs: FadeBasicDapSessionLogPaths): File {
        val anchor = sessionLogs.dapLogPath.takeIf { it.isNotBlank() }
            ?: sessionLogs.debuggerLogPath.takeIf { it.isNotBlank() }
        return if (anchor != null) {
            val parent = File(anchor).parentFile ?: File(System.getProperty("java.io.tmpdir"))
            val baseName = File(anchor).nameWithoutExtension.replaceFirst(
                Regex("^fade-(dap|debugger)-"), "fade-dap-stderr-"
            ).ifBlank { "fade-dap-stderr-${System.currentTimeMillis()}" }
            File(parent, "$baseName.log").also { it.parentFile?.mkdirs() }
        } else {
            File.createTempFile("fade-dap-stderr-", ".log")
        }
    }
}

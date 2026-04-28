package ink.brewed.fadebasic.rider.lsp

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.openapi.application.PathManager
import com.intellij.openapi.project.Project
import com.intellij.openapi.project.guessProjectDir
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.platform.lsp.api.LspServerSupportProvider
import com.intellij.platform.lsp.api.ProjectWideLspServerDescriptor
import ink.brewed.fadebasic.rider.FadeBasicDevPaths
import ink.brewed.fadebasic.rider.FadeBasicFileType
import ink.brewed.fadebasic.rider.FadeBasicToolingPaths
import ink.brewed.fadebasic.rider.settings.FadeBasicRiderSettings
import java.io.File

/**
 * Starts the Fade Basic LSP as a child `dotnet` process.
 *
 * **Transport:** IntelliJ’s LSP client always speaks JSON-RPC over the process **stdin/stdout** (stdio).
 * VS Code’s `TransportKind.pipe` uses OS named pipes where the **server** is a pipe *client* (`NamedPipeClientStream`);
 * that mode is not exposed by `ProjectWideLspServerDescriptor`, so Rider cannot mirror pipe transport without a custom
 * client. The Fade Basic server already uses stdio when no `--pipe=` argument is present.
 *
 * **Argv:** `dotnet run --project <absolute LSP.csproj> --` plus optional `--use-log-path` (Gradle-baked csproj path
 * from [FadeBasicDevPaths.LSP_PROJECT]). When logging is on, cwd is [defaultFadeBasicLspLogDirectory] (a `fade-basic-lsp`
 * folder under the IDE log directory; open the log tree via **Help → Diagnostic Tools → Show Log**) so Serilog writes
 * `fadeLsp*.txt` there.
 */
class FadeBasicLspServerSupportProvider : LspServerSupportProvider {

    override fun fileOpened(project: Project, file: VirtualFile, serverStarter: LspServerSupportProvider.LspServerStarter) {
        if (!FadeBasicFileType.isFadeBasic(file)) return
        val st = FadeBasicRiderSettings.getInstance().state
        val cl = FadeBasicLspServerSupportProvider::class.java.classLoader
        if (!FadeBasicToolingPaths.canStartLsp(st, cl)) return
        serverStarter.ensureServerStarted(FadeBasicLspServerDescriptor(project))
    }

    private class FadeBasicLspServerDescriptor(project: Project) : ProjectWideLspServerDescriptor(project, "Fade Basic") {

        override fun isSupportedFile(file: VirtualFile): Boolean = FadeBasicFileType.isFadeBasic(file)

        override fun createCommandLine(): GeneralCommandLine {
            val st = FadeBasicRiderSettings.getInstance().state
            val dotnet = st.dotnetPath.ifBlank { "dotnet" }
            val cl = FadeBasicLspServerSupportProvider::class.java.classLoader
            val argv = FadeBasicToolingPaths.lspArgv(st, cl)
            val line = GeneralCommandLine().withExePath(dotnet).withParameters(argv)
            line.withParentEnvironmentType(GeneralCommandLine.ParentEnvironmentType.CONSOLE)
            if (st.lspWriteLogFile) {
                val logDir = defaultFadeBasicLspLogDirectory()
                logDir.mkdirs()
                line.workDirectory = logDir
            } else {
                val root = project.guessProjectDir()?.path ?: project.basePath
                if (!root.isNullOrBlank()) {
                    line.workDirectory = File(root)
                }
            }
            return line
        }
    }
}

/** Writable folder for `fadeLsp*.txt` when `--use-log-path` is used (next to other IDE logs). */
internal fun defaultFadeBasicLspLogDirectory(): File =
    File(PathManager.getLogPath(), "fade-basic-lsp")

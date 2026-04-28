package ink.brewed.fadebasic.rider.run

import com.intellij.openapi.application.PathManager
import ink.brewed.fadebasic.rider.settings.FadeBasicRiderSettings
import java.nio.file.Files
import java.nio.file.Path
import java.time.LocalDateTime
import java.time.format.DateTimeFormatter

/**
 * Effective log paths for one debug session (adapter env + DAP `launch` args must match).
 *
 * When [FadeBasicRiderSettings.State.dapDefaultLogsWhenPathsBlank] is true, blank run-configuration paths
 * are filled with files under the IDE log root (`fade-basic-dap/`), so the DAP host can open [DAPLogger] without
 * changing the DAP project.
 */
data class FadeBasicDapSessionLogPaths(
    val debuggerLogPath: String,
    val dapLogPath: String,
    /** True when at least one path was filled from IDE defaults for this session. */
    val usedIdeDefaultFiles: Boolean,
) {
    companion object {
        private val STAMP: DateTimeFormatter = DateTimeFormatter.ofPattern("yyyyMMdd-HHmmss")

        fun resolve(
            settings: FadeBasicRiderSettings.State,
            configuration: FadeBasicDapRunConfiguration,
        ): FadeBasicDapSessionLogPaths {
            val dbgIn = configuration.debuggerLogPath.trim()
            val dapIn = configuration.dapLogPath.trim()
            if (!settings.dapDefaultLogsWhenPathsBlank) {
                return FadeBasicDapSessionLogPaths(dbgIn, dapIn, false)
            }
            val needDbg = dbgIn.isEmpty()
            val needDap = dapIn.isEmpty()
            if (!needDbg && !needDap) {
                return FadeBasicDapSessionLogPaths(dbgIn, dapIn, false)
            }
            val root: Path = Path.of(PathManager.getLogPath(), "fade-basic-dap")
            Files.createDirectories(root)
            val stamp = LocalDateTime.now().format(STAMP) + "-" + System.nanoTime().toString().takeLast(6)
            val effDbg =
                if (needDbg) {
                    root.resolve("fade-debugger-$stamp.log").toAbsolutePath().normalize().toString()
                } else {
                    dbgIn
                }
            val effDap =
                if (needDap) {
                    root.resolve("fade-dap-$stamp.log").toAbsolutePath().normalize().toString()
                } else {
                    dapIn
                }
            return FadeBasicDapSessionLogPaths(effDbg, effDap, needDbg || needDap)
        }
    }
}

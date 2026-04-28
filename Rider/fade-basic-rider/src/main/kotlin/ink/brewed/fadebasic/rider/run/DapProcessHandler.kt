package ink.brewed.fadebasic.rider.run

import com.intellij.execution.process.ProcessHandler
import java.io.OutputStream

/**
 * Minimal [ProcessHandler] for the DAP adapter process.
 *
 * Does NOT read stdout — it's reserved for the DAP JSON-RPC channel that lsp4j reads
 * directly from the [Process]. Adapter stderr is redirected to a file by
 * [FadeBasicDapDebugProgramRunner], not piped here, so a .NET abort still leaves a
 * complete stack trace.
 */
class DapProcessHandler(private val process: Process) : ProcessHandler() {

    override fun destroyProcessImpl() {
        process.destroyForcibly()
        notifyProcessTerminated(0)
    }

    override fun detachProcessImpl() {
        process.destroyForcibly()
        notifyProcessTerminated(0)
    }

    override fun detachIsDefault(): Boolean = false

    override fun getProcessInput(): OutputStream? = process.outputStream

    fun javaProcess(): Process = process

    /** True iff the underlying OS process is still alive. */
    fun isProcessAlive(): Boolean = process.isAlive

    /** Public wrapper so external code can signal termination. */
    fun fireTerminated(exitCode: Int) {
        notifyProcessTerminated(exitCode)
    }
}

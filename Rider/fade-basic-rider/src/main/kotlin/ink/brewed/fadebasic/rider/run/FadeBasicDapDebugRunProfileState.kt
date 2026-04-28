package ink.brewed.fadebasic.rider.run

import com.intellij.execution.DefaultExecutionResult
import com.intellij.execution.ExecutionException
import com.intellij.execution.ExecutionResult
import com.intellij.execution.Executor
import com.intellij.execution.configurations.RunProfileState
import com.intellij.execution.runners.ExecutionEnvironment
import com.intellij.execution.runners.ProgramRunner

/**
 * Debug profile state. The actual debug session setup is handled by [FadeBasicDapDebugProgramRunner],
 * which calls [com.intellij.xdebugger.XDebuggerManager.startSession] directly.
 * This state is only used as a fallback if the runner somehow calls execute().
 */
class FadeBasicDapDebugRunProfileState(
    private val environment: ExecutionEnvironment,
    private val configuration: FadeBasicDapRunConfiguration,
) : RunProfileState {

    override fun execute(executor: Executor?, runner: ProgramRunner<*>): ExecutionResult {
        throw ExecutionException("FadeBasicDapDebugRunProfileState.execute() should not be called directly. Use FadeBasicDapDebugProgramRunner.")
    }
}

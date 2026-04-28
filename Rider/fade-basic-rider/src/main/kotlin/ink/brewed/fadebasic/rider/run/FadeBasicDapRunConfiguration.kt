package ink.brewed.fadebasic.rider.run

import com.intellij.execution.Executor
import com.intellij.execution.configurations.LocatableConfigurationBase
import com.intellij.execution.configurations.RuntimeConfigurationError
import com.intellij.execution.configurations.RunConfiguration
import com.intellij.execution.configurations.RunProfileState
import com.intellij.execution.executors.DefaultDebugExecutor
import com.intellij.execution.runners.ExecutionEnvironment
import com.intellij.openapi.options.SettingsEditor
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.JDOMExternalizerUtil
import org.jdom.Element

class FadeBasicDapRunConfiguration(
    project: Project,
    factory: com.intellij.execution.configurations.ConfigurationFactory,
    name: String,
) : LocatableConfigurationBase<FadeBasicDapRunConfiguration>(project, factory, name) {

    enum class Mode { LAUNCH, ATTACH }

    var mode: Mode = Mode.LAUNCH
    var program: String = ""
    var waitForDebugger: Boolean = false
    /** Port of an already-running Fade debug server. Only used when [mode] is [Mode.ATTACH]. */
    var debugPort: Int = 0
    var debuggerLogPath: String = ""
    var dapLogPath: String = ""

    override fun writeExternal(element: Element) {
        super.writeExternal(element)
        JDOMExternalizerUtil.writeField(element, "mode", mode.name)
        JDOMExternalizerUtil.writeField(element, "program", program)
        JDOMExternalizerUtil.writeField(element, "waitForDebugger", waitForDebugger.toString())
        JDOMExternalizerUtil.writeField(element, "debugPort", debugPort.toString())
        JDOMExternalizerUtil.writeField(element, "debuggerLogPath", debuggerLogPath)
        JDOMExternalizerUtil.writeField(element, "dapLogPath", dapLogPath)
    }

    override fun readExternal(element: Element) {
        super.readExternal(element)
        mode = JDOMExternalizerUtil.readField(element, "mode")
            ?.let { runCatching { Mode.valueOf(it) }.getOrNull() } ?: Mode.LAUNCH
        program = JDOMExternalizerUtil.readField(element, "program") ?: ""
        waitForDebugger = JDOMExternalizerUtil.readField(element, "waitForDebugger")?.toBooleanStrictOrNull() ?: false
        debugPort = JDOMExternalizerUtil.readField(element, "debugPort")?.toIntOrNull() ?: 0
        debuggerLogPath = JDOMExternalizerUtil.readField(element, "debuggerLogPath") ?: ""
        dapLogPath = JDOMExternalizerUtil.readField(element, "dapLogPath") ?: ""
    }

    override fun clone(): RunConfiguration {
        @Suppress("UNCHECKED_CAST")
        val copy = super.clone() as FadeBasicDapRunConfiguration
        copy.mode = mode
        copy.program = program
        copy.waitForDebugger = waitForDebugger
        copy.debugPort = debugPort
        copy.debuggerLogPath = debuggerLogPath
        copy.dapLogPath = dapLogPath
        return copy
    }

    /** User-entered path, or a single discovered `.csproj` under the project root when blank. */
    fun resolvedProgramPath(): String {
        val trimmed = program.trim()
        if (trimmed.isNotBlank()) return trimmed
        return FadeBasicDapProgramDiscoverer.defaultProgramOrEmpty(project)
    }

    override fun checkConfiguration() {
        val resolved = resolvedProgramPath()
        if (resolved.isBlank()) {
            throw RuntimeConfigurationError(
                "Program is required: set the path to the target .csproj, or open a project folder that contains exactly one .csproj (auto-detected).",
            )
        }
        if (mode == Mode.ATTACH && (debugPort <= 0 || debugPort > 65535)) {
            throw RuntimeConfigurationError(
                "Attach requires a valid TCP port. Use the Discover... button to pick a running Fade debug server.",
            )
        }
    }

    /**
     * **Run** (green): runs your Fade Basic app with `dotnet run --project …` (no DAP, no debugger).
     * **Debug** (bug): starts the Fade DAP adapter and attaches the DAP client (breakpoints, stepping, evaluate).
     */
    override fun getState(executor: Executor, environment: ExecutionEnvironment): RunProfileState =
        if (executor.id == DefaultDebugExecutor.EXECUTOR_ID) {
            FadeBasicDapDebugRunProfileState(environment, this)
        } else {
            FadeBasicDapNonDebugRunProfileState(environment, this)
        }

    override fun getConfigurationEditor(): SettingsEditor<out FadeBasicDapRunConfiguration> =
        FadeBasicDapSettingsEditor()
}

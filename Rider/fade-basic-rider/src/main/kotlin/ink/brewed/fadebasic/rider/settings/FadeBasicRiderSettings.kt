package ink.brewed.fadebasic.rider.settings

import com.intellij.openapi.components.PersistentStateComponent
import com.intellij.openapi.components.Service
import com.intellij.openapi.components.State
import com.intellij.openapi.components.Storage
import com.intellij.openapi.components.service
import com.intellij.util.xmlb.XmlSerializerUtil

@Service(Service.Level.APP)
@State(name = "FadeBasicRiderSettings", storages = [Storage("fadeBasicRider.xml")])
class FadeBasicRiderSettings : PersistentStateComponent<FadeBasicRiderSettings.State> {

    data class State(
        /**
         * Bumped when persisted settings need a one-time migration. `< 2` re-enables LSP file logging for XML that
         * predates [lspWriteLogFile] (missing property deserialized as false).
         */
        var settingsSchemaVersion: Int = 3,
        var dotnetPath: String = "dotnet",
        var lspDllPath: String = "",
        var lspProjectPath: String = "",
        var dapDllPath: String = "",
        var dapProjectPath: String = "",
        /**
         * When true, appends `--use-log-path` and sets the LSP process cwd to the IDE log tree
         * (`PathManager.getLogPath()/fade-basic-lsp`) so Serilog writes rolling `fadeLsp*.txt` there (VS Code: `lsp.useLogFile`).
         */
        var lspWriteLogFile: Boolean = true,
        /**
         * When true, a Fade Basic **Debug** session with empty "Debugger log" / "DAP log" fields in the run
         * configuration gets default files under the IDE log directory (`fade-basic-dap/` next to other IDE logs).
         */
        var dapDefaultLogsWhenPathsBlank: Boolean = true,
    )

    private var internalState = State()

    override fun getState(): State = internalState

    override fun loadState(state: State) {
        XmlSerializerUtil.copyBean(state, internalState)
        if (internalState.settingsSchemaVersion < 2) {
            internalState.lspWriteLogFile = true
            internalState.settingsSchemaVersion = 2
        }
        if (internalState.settingsSchemaVersion < 3) {
            internalState.dapDefaultLogsWhenPathsBlank = true
            internalState.settingsSchemaVersion = 3
        }
    }

    companion object {
        fun getInstance(): FadeBasicRiderSettings = service()
    }
}

package ink.brewed.fadebasic.rider.settings

import com.intellij.openapi.options.Configurable
import com.intellij.util.ui.FormBuilder
import javax.swing.JCheckBox
import javax.swing.JComponent
import javax.swing.JPanel
import javax.swing.JTextField

class FadeBasicRiderConfigurable : Configurable {

    private val settings = FadeBasicRiderSettings.getInstance()
    private lateinit var dotnetField: JTextField
    private lateinit var lspDllField: JTextField
    private lateinit var lspProjectField: JTextField
    private lateinit var dapDllField: JTextField
    private lateinit var dapProjectField: JTextField
    private lateinit var lspLogFileCheckbox: JCheckBox
    private lateinit var dapDefaultLogsCheckbox: JCheckBox

    override fun getDisplayName(): String = "Fade Basic"

    override fun createComponent(): JComponent {
        dotnetField = JTextField()
        lspDllField = JTextField()
        lspProjectField = JTextField()
        dapDllField = JTextField()
        dapProjectField = JTextField()
        lspLogFileCheckbox =
            JCheckBox(
                "Write LSP log file (fadeLsp*.txt under Help → Diagnostic Tools → Show Log → fade-basic-lsp)",
            )
        dapDefaultLogsCheckbox =
            JCheckBox(
                "When debugging, if run configuration log paths are empty, write DAP / debugger logs under " +
                    "Help → Diagnostic Tools → Show Log → fade-basic-dap",
            )
        reset()
        return FormBuilder.createFormBuilder()
            .addLabeledComponent("Dotnet executable", dotnetField)
            .addLabeledComponent("Path to LSP.dll (optional if project set)", lspDllField)
            .addLabeledComponent("Path to LSP.csproj (dev)", lspProjectField)
            .addComponent(lspLogFileCheckbox)
            .addLabeledComponent("Path to DAP.dll (optional if project set)", dapDllField)
            .addLabeledComponent("Path to DAP.csproj (dev)", dapProjectField)
            .addComponent(dapDefaultLogsCheckbox)
            .addComponentFillVertically(JPanel(), 0)
            .panel
    }

    override fun isModified(): Boolean {
        val s = settings.state
        return dotnetField.text.trim() != s.dotnetPath.trim() ||
            lspDllField.text.trim() != s.lspDllPath.trim() ||
            lspProjectField.text.trim() != s.lspProjectPath.trim() ||
            dapDllField.text.trim() != s.dapDllPath.trim() ||
            dapProjectField.text.trim() != s.dapProjectPath.trim() ||
            lspLogFileCheckbox.isSelected != s.lspWriteLogFile ||
            dapDefaultLogsCheckbox.isSelected != s.dapDefaultLogsWhenPathsBlank
    }

    override fun apply() {
        val s = settings.state
        s.dotnetPath = dotnetField.text.trim()
        s.lspDllPath = lspDllField.text.trim()
        s.lspProjectPath = lspProjectField.text.trim()
        s.dapDllPath = dapDllField.text.trim()
        s.dapProjectPath = dapProjectField.text.trim()
        s.lspWriteLogFile = lspLogFileCheckbox.isSelected
        s.dapDefaultLogsWhenPathsBlank = dapDefaultLogsCheckbox.isSelected
    }

    override fun reset() {
        val s = settings.state
        dotnetField.text = s.dotnetPath
        lspDllField.text = s.lspDllPath
        lspProjectField.text = s.lspProjectPath
        dapDllField.text = s.dapDllPath
        dapProjectField.text = s.dapProjectPath
        lspLogFileCheckbox.isSelected = s.lspWriteLogFile
        dapDefaultLogsCheckbox.isSelected = s.dapDefaultLogsWhenPathsBlank
    }
}

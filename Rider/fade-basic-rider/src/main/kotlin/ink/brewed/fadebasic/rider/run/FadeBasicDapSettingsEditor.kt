package ink.brewed.fadebasic.rider.run

import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.options.SettingsEditor
import com.intellij.openapi.ui.ComboBox
import com.intellij.openapi.ui.Messages
import com.intellij.util.ui.FormBuilder
import java.awt.BorderLayout
import java.awt.FlowLayout
import javax.swing.ButtonGroup
import javax.swing.DefaultComboBoxModel
import javax.swing.JButton
import javax.swing.JCheckBox
import javax.swing.JComponent
import javax.swing.JPanel
import javax.swing.JRadioButton
import javax.swing.JTextField

class FadeBasicDapSettingsEditor : SettingsEditor<FadeBasicDapRunConfiguration>() {

    private val launchRadio = JRadioButton("Launch", true)
    private val attachRadio = JRadioButton("Attach to running Fade debug server")
    private val modeGroup = ButtonGroup().apply {
        add(launchRadio); add(attachRadio)
    }

    private val programCombo = ComboBox<String>().apply { isEditable = true }
    private val waitForDebuggerBox =
        JCheckBox("Wait for .NET debugger on DAP startup (FADE_WAIT_FOR_DEBUG)")
    private val debugPortField = JTextField()
    private val discoverButton = JButton("Discover…")
    private val debuggerLogField = JTextField()
    private val dapLogField = JTextField()

    private fun updateModeUiState() {
        val attach = attachRadio.isSelected
        debugPortField.isEnabled = attach
        discoverButton.isEnabled = attach
        // Wait-for-debugger only matters for launch (the env-var is consumed by the spawned debuggee).
        waitForDebuggerBox.isEnabled = !attach
    }

    override fun createEditor(): JComponent {
        launchRadio.addActionListener { updateModeUiState() }
        attachRadio.addActionListener { updateModeUiState() }
        discoverButton.addActionListener { runDiscovery() }

        val modePanel = JPanel(FlowLayout(FlowLayout.LEFT, 0, 0)).apply {
            add(launchRadio); add(attachRadio)
        }
        val programPanel = JPanel(BorderLayout())
        programPanel.add(programCombo, BorderLayout.CENTER)
        val portPanel = JPanel(BorderLayout(8, 0)).apply {
            add(debugPortField, BorderLayout.CENTER)
            add(discoverButton, BorderLayout.EAST)
        }
        return FormBuilder.createFormBuilder()
            .addLabeledComponent("Mode", modePanel)
            .addLabeledComponent("Program (.csproj path)", programPanel)
            .addLabeledComponent("Debug port (attach only)", portPanel)
            .addComponent(waitForDebuggerBox)
            .addLabeledComponent("Debugger log path (optional, FADE_DEBUGGER_LOG_PATH)", debuggerLogField)
            .addLabeledComponent("DAP log path (optional, FADE_DAP_LOG_PATH)", dapLogField)
            .addComponentFillVertically(JPanel(), 0)
            .panel
    }

    override fun resetEditorFrom(s: FadeBasicDapRunConfiguration) {
        when (s.mode) {
            FadeBasicDapRunConfiguration.Mode.LAUNCH -> launchRadio.isSelected = true
            FadeBasicDapRunConfiguration.Mode.ATTACH -> attachRadio.isSelected = true
        }
        val discovered = FadeBasicDapProgramDiscoverer.discoverCsprojPaths(s.project)
        val current = s.program.trim()
        val items = (discovered + current).filter { it.isNotBlank() }.distinct().sorted()
        programCombo.model = DefaultComboBoxModel(items.toTypedArray())
        when {
            current.isNotBlank() -> programCombo.item = current
            discovered.size == 1 -> programCombo.item = discovered.single()
            else -> programCombo.selectedItem = null
        }
        waitForDebuggerBox.isSelected = s.waitForDebugger
        debugPortField.text = if (s.debugPort > 0) s.debugPort.toString() else ""
        debuggerLogField.text = s.debuggerLogPath
        dapLogField.text = s.dapLogPath
        // Reset transient discovery UI state — the editor instance can be reused across
        // dialog opens, so without this the button can be stuck at "Discovering…" if the
        // previous session was closed mid-discovery.
        discoverButton.text = "Discover…"
        updateModeUiState()
    }

    override fun applyEditorTo(s: FadeBasicDapRunConfiguration) {
        s.mode = if (attachRadio.isSelected) {
            FadeBasicDapRunConfiguration.Mode.ATTACH
        } else {
            FadeBasicDapRunConfiguration.Mode.LAUNCH
        }
        s.program = programPathFromUi()
        s.waitForDebugger = waitForDebuggerBox.isSelected
        s.debugPort = debugPortField.text.trim().toIntOrNull() ?: 0
        s.debuggerLogPath = debuggerLogField.text.trim()
        s.dapLogPath = dapLogField.text.trim()
    }

    /** Prefer the editable field text (what the user typed), then the selected list item. */
    private fun programPathFromUi(): String {
        val fromTextField = (programCombo.editor.editorComponent as? JTextField)?.text?.trim().orEmpty()
        val fromSelection = (programCombo.selectedItem as? String)?.trim().orEmpty()
        return fromTextField.ifBlank { fromSelection }
    }

    private fun runDiscovery() {
        discoverButton.isEnabled = false
        discoverButton.text = "Discovering…"
        // Capture the modality state of the Settings dialog so EDT callbacks fire
        // while the modal is still open — without this, invokeLater queues at the
        // non-modal state and the callback runs only after the user closes the dialog.
        val modality = com.intellij.openapi.application.ModalityState.stateForComponent(discoverButton)
        ApplicationManager.getApplication().executeOnPooledThread {
            val servers = try {
                FadeBasicDapDebugServerDiscovery.discover()
            } catch (_: Exception) {
                emptyList()
            }
            ApplicationManager.getApplication().invokeLater({
                discoverButton.text = "Discover…"
                discoverButton.isEnabled = attachRadio.isSelected
                if (servers.isEmpty()) {
                    Messages.showInfoMessage(
                        debugPortField,
                        "No running Fade debug servers responded on the local network.\n" +
                            "Start your Fade Basic program with FADE_WAIT_FOR_DEBUG=true and try again.",
                        "Fade Basic — Discover",
                    )
                    return@invokeLater
                }
                val labels = servers.map { it.displayLabel() }.toTypedArray()
                val choice = Messages.showChooseDialog(
                    null as com.intellij.openapi.project.Project?,
                    "Pick a running Fade debug server:",
                    "Fade Basic — Discover",
                    null,
                    labels,
                    labels[0],
                )
                if (choice >= 0) {
                    debugPortField.text = servers[choice].port.toString()
                }
            }, modality)
        }
    }
}

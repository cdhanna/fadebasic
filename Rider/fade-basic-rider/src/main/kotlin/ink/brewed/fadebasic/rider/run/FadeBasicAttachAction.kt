package ink.brewed.fadebasic.rider.run

import com.intellij.execution.ProgramRunnerUtil
import com.intellij.execution.RunManager
import com.intellij.execution.configurations.ConfigurationTypeUtil
import com.intellij.execution.executors.DefaultDebugExecutor
import com.intellij.openapi.actionSystem.AnAction
import com.intellij.openapi.actionSystem.AnActionEvent
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.application.ModalityState
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.progress.ProgressIndicator
import com.intellij.openapi.progress.ProgressManager
import com.intellij.openapi.progress.Task
import com.intellij.openapi.project.Project
import com.intellij.openapi.ui.Messages
import com.intellij.ui.SimpleTextAttributes
import com.intellij.ui.ColoredListCellRenderer
import com.intellij.openapi.ui.popup.JBPopupFactory

/**
 * Run-menu entry that discovers running Fade Basic debug servers via UDP broadcast and starts
 * an attach debug session against the user's pick. Idiomatic counterpart to "Attach to Unity
 * Editor & Play" — the standard `XAttachDebuggerProvider` framework targets local OS processes
 * and isn't a good fit for our LAN-discoverable game runtimes.
 */
class FadeBasicAttachAction : AnAction() {
    private val log = logger<FadeBasicAttachAction>()

    override fun update(e: AnActionEvent) {
        e.presentation.isEnabledAndVisible = e.project != null
    }

    override fun actionPerformed(e: AnActionEvent) {
        val project = e.project ?: return
        ProgressManager.getInstance().run(object : Task.Backgroundable(project, "Discovering Fade debug servers", true) {
            override fun run(indicator: ProgressIndicator) {
                indicator.isIndeterminate = true
                val servers = try {
                    FadeBasicDapDebugServerDiscovery.discover()
                } catch (ex: Exception) {
                    log.warn("Fade discovery failed", ex)
                    emptyList()
                }
                ApplicationManager.getApplication().invokeLater(
                    { onDiscoveryDone(project, servers) },
                    ModalityState.defaultModalityState(),
                )
            }
        })
    }

    private fun onDiscoveryDone(project: Project, servers: List<FadeBasicDapDebugServerDiscovery.Server>) {
        if (servers.isEmpty()) {
            Messages.showInfoMessage(
                project,
                "No running Fade debug servers responded on the local network.\n" +
                    "Start your Fade Basic program with FADE_WAIT_FOR_DEBUG=true and try again.",
                "Attach to Fade Process",
            )
            return
        }
        if (servers.size == 1) {
            launchAttach(project, servers.single())
            return
        }
        showPicker(project, servers)
    }

    private fun showPicker(project: Project, servers: List<FadeBasicDapDebugServerDiscovery.Server>) {
        // Disambiguate the SAM conversion: PopupChooserBuilder has both a Runnable- and a
        // (IntelliJ's) Consumer<T>-based setItemChosenCallback overload. Picking the Consumer
        // overload by typing the lambda explicitly also routes us to the IPopupChooserBuilder
        // return type that has createPopup() — the Runnable overload returns an older builder
        // without it. Note: `com.intellij.util.Consumer`, not `java.util.function.Consumer`.
        val onChosen = com.intellij.util.Consumer<FadeBasicDapDebugServerDiscovery.Server> { picked ->
            launchAttach(project, picked)
        }
        JBPopupFactory.getInstance()
            .createPopupChooserBuilder(servers)
            .setTitle("Attach to Fade Process")
            .setRenderer(ServerCellRenderer())
            .setItemChosenCallback(onChosen)
            .createPopup()
            .showCenteredInCurrentWindow(project)
    }

    private fun launchAttach(project: Project, server: FadeBasicDapDebugServerDiscovery.Server) {
        val configType = ConfigurationTypeUtil.findConfigurationType(FadeBasicDapConfigurationType::class.java)
        if (configType == null) {
            Messages.showErrorDialog(project, "Fade Basic run configuration type is not registered.", "Attach to Fade Process")
            return
        }
        val factory = configType.configurationFactories.firstOrNull() ?: run {
            Messages.showErrorDialog(project, "Fade Basic configuration factory missing.", "Attach to Fade Process")
            return
        }
        val title = server.primaryTitle()
        val name = "Attach to Fade — $title :${server.port}"
        val runManager = RunManager.getInstance(project)
        val settings = runManager.createConfiguration(name, factory)
        // Mark as temporary so it doesn't pollute the persistent run-config list.
        settings.isTemporary = true
        val cfg = settings.configuration as FadeBasicDapRunConfiguration
        cfg.mode = FadeBasicDapRunConfiguration.Mode.ATTACH
        cfg.debugPort = server.port
        // Leave `program` blank so resolvedProgramPath() falls back to project auto-discovery.
        // Surface a friendly error if there's no usable .csproj (rather than the generic
        // RuntimeConfigurationError thrown deep inside the framework).
        if (cfg.resolvedProgramPath().isBlank()) {
            Messages.showErrorDialog(
                project,
                "Couldn't determine a target .csproj for the attached game.\n" +
                    "Open the Run Configurations dialog and create an Attach configuration with the program path filled in.",
                "Attach to Fade Process",
            )
            return
        }
        ProgramRunnerUtil.executeConfiguration(settings, DefaultDebugExecutor.getDebugExecutorInstance())
    }

    /** Two-line cell: title (process name) on top, host:port · pid · label on bottom. */
    private class ServerCellRenderer : ColoredListCellRenderer<FadeBasicDapDebugServerDiscovery.Server>() {
        override fun customizeCellRenderer(
            list: javax.swing.JList<out FadeBasicDapDebugServerDiscovery.Server>,
            value: FadeBasicDapDebugServerDiscovery.Server?,
            index: Int,
            selected: Boolean,
            hasFocus: Boolean,
        ) {
            if (value == null) return
            append(value.primaryTitle(), SimpleTextAttributes.REGULAR_ATTRIBUTES)
            if (value.processName.isNotBlank() && value.processName != value.primaryTitle()) {
                append("  (${value.processName})", SimpleTextAttributes.GRAYED_ATTRIBUTES)
            }
            append("    ${value.host}:${value.port}", SimpleTextAttributes.GRAYED_BOLD_ATTRIBUTES)
            if (value.processId > 0) {
                append("    pid ${value.processId}", SimpleTextAttributes.GRAYED_ATTRIBUTES)
            }
            if (value.label.isNotBlank()) {
                append("    ${value.label}", SimpleTextAttributes.GRAYED_ITALIC_ATTRIBUTES)
            }
        }
    }
}

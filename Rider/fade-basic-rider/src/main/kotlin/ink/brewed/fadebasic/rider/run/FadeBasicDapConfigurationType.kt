package ink.brewed.fadebasic.rider.run

import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.execution.configurations.ConfigurationType
import com.intellij.icons.AllIcons

class FadeBasicDapConfigurationType : ConfigurationType {

    private val factory = FadeBasicDapConfigurationFactory(this)

    override fun getDisplayName(): String = "Fade Basic (DAP)"

    override fun getConfigurationTypeDescription(): String =
        "Run launches your app with dotnet run (no debugger). Debug starts the Fade DAP adapter and attaches the debugger."

    override fun getIcon() = AllIcons.Actions.StartDebugger

    override fun getId(): String = "FadeBasicDapConfiguration"

    override fun getConfigurationFactories(): Array<ConfigurationFactory> = arrayOf(factory)
}

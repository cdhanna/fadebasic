package ink.brewed.fadebasic.rider.run

import com.intellij.execution.configurations.ConfigurationFactory
import com.intellij.execution.configurations.ConfigurationType
import com.intellij.execution.configurations.RunConfiguration
import com.intellij.openapi.project.Project

class FadeBasicDapConfigurationFactory(type: ConfigurationType) : ConfigurationFactory(type) {

    override fun getId(): String = "FadeBasicDapFactory"

    override fun getName(): String = "Fade Basic DAP"

    override fun createTemplateConfiguration(project: Project): RunConfiguration =
        FadeBasicDapRunConfiguration(project, this, "Fade Basic DAP").apply {
            program = FadeBasicDapProgramDiscoverer.defaultProgramOrEmpty(project)
        }
}

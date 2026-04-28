package ink.brewed.fadebasic.rider.debug

import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile
import com.intellij.xdebugger.breakpoints.XLineBreakpointType
import ink.brewed.fadebasic.rider.FadeBasicFileType

class FadeBasicLineBreakpointType : XLineBreakpointType<FadeBasicBpProps>("fade-basic-line", "Fade Basic Line") {

    override fun createBreakpointProperties(file: VirtualFile, line: Int): FadeBasicBpProps = FadeBasicBpProps()

    override fun canPutAt(file: VirtualFile, line: Int, project: Project): Boolean = FadeBasicFileType.isFadeBasic(file)
}

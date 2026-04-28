package ink.brewed.fadebasic.rider.debug

import com.intellij.xdebugger.breakpoints.XBreakpointProperties

class FadeBasicBpProps : XBreakpointProperties<FadeBasicBpProps>() {
    override fun getState(): FadeBasicBpProps = this

    override fun loadState(state: FadeBasicBpProps) {
        // No persisted fields
    }
}

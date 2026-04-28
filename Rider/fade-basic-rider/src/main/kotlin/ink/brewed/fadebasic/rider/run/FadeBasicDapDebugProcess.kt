package ink.brewed.fadebasic.rider.run

import com.intellij.execution.process.BaseProcessHandler
import com.intellij.execution.process.ProcessAdapter
import com.intellij.execution.process.ProcessEvent
import com.intellij.execution.process.ProcessHandler
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.ui.MessageType
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.fileTypes.PlainTextFileType
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.LocalFileSystem
import com.intellij.util.concurrency.AppExecutorUtil
import com.intellij.xdebugger.XDebugProcess
import com.intellij.xdebugger.XDebugSession
import com.intellij.xdebugger.XDebuggerUtil
import com.intellij.xdebugger.XSourcePosition
import com.intellij.xdebugger.breakpoints.XBreakpointHandler
import com.intellij.xdebugger.breakpoints.XLineBreakpoint
import com.intellij.xdebugger.evaluation.XDebuggerEvaluator
import com.intellij.xdebugger.evaluation.XDebuggerEvaluator.XEvaluationCallback
import com.intellij.xdebugger.frame.XCompositeNode
import com.intellij.xdebugger.frame.XExecutionStack
import com.intellij.xdebugger.frame.XStackFrame
import com.intellij.xdebugger.frame.XExecutionStack.XStackFrameContainer
import com.intellij.xdebugger.frame.XSuspendContext
import com.intellij.xdebugger.frame.XValue
import com.intellij.xdebugger.frame.XValueChildrenList
import com.intellij.xdebugger.frame.XValueNode
import com.intellij.xdebugger.frame.XValuePlace
import com.intellij.xdebugger.frame.presentation.XStringValuePresentation
import com.intellij.xdebugger.evaluation.XDebuggerEditorsProvider
import ink.brewed.fadebasic.rider.debug.FadeBasicBpProps
import ink.brewed.fadebasic.rider.debug.FadeBasicLineBreakpointType
import org.eclipse.lsp4j.debug.ContinueArguments
import org.eclipse.lsp4j.debug.DisconnectArguments
import org.eclipse.lsp4j.debug.EvaluateArguments
import org.eclipse.lsp4j.debug.EvaluateArgumentsContext
import org.eclipse.lsp4j.debug.NextArguments
import org.eclipse.lsp4j.debug.PauseArguments
import org.eclipse.lsp4j.debug.ScopesArguments
import org.eclipse.lsp4j.debug.StackFrame
import org.eclipse.lsp4j.debug.StackTraceArguments
import org.eclipse.lsp4j.debug.StepInArguments
import org.eclipse.lsp4j.debug.StepOutArguments
import org.eclipse.lsp4j.debug.StoppedEventArguments
import org.eclipse.lsp4j.debug.VariablesArguments
import org.eclipse.lsp4j.debug.launch.DSPLauncher
import org.eclipse.lsp4j.debug.services.IDebugProtocolServer
import org.eclipse.lsp4j.jsonrpc.Launcher
import java.nio.file.Paths
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicReference

class FadeBasicDapDebugProcess(
    session: XDebugSession,
    private val dapHandler: ProcessHandler,
    private val configuration: FadeBasicDapRunConfiguration,
    private val sessionLogPaths: FadeBasicDapSessionLogPaths,
) : XDebugProcess(session) {

    private val log = logger<FadeBasicDapDebugProcess>()
    private val editorsProvider =
        object : XDebuggerEditorsProvider() {
            override fun getFileType() = PlainTextFileType.INSTANCE
            override fun createDocument(
                project: Project,
                expression: String,
                sourcePosition: XSourcePosition?,
                mode: com.intellij.xdebugger.evaluation.EvaluationMode,
            ): com.intellij.openapi.editor.Document {
                return com.intellij.openapi.editor.EditorFactory.getInstance().createDocument(expression)
            }
        }

    private val launcherRef = AtomicReference<Launcher<IDebugProtocolServer>?>(null)
    private val remoteRef = AtomicReference<IDebugProtocolServer?>(null)
    private val lastThreadId = AtomicInteger(-1)
    private val debugConsole = com.intellij.execution.filters.TextConsoleBuilderFactory.getInstance()
        .createBuilder(configuration.project).console

    private val dapClient =
        FadeBasicDapLsp4jClient(
            configuration.project,
            onStopped = { args -> ApplicationManager.getApplication().invokeLater { handleStopped(args) } },
            onTerminated = { ApplicationManager.getApplication().invokeLater { session.stop() } },
            onDebuggeeOutput = { text, outputType ->
                debugConsole.print(text, consoleTypeForProcessOutput(outputType))
            },
            onAdapterOutput = { text, category ->
                debugConsole.print(text, consoleTypeForDapCategory(category))
            },
        )

    private val breakpointHandler =
        object : XBreakpointHandler<XLineBreakpoint<FadeBasicBpProps>>(FadeBasicLineBreakpointType::class.java) {
            override fun registerBreakpoint(breakpoint: XLineBreakpoint<FadeBasicBpProps>) {
                pushBreakpointsForFileFromIde(breakpoint)
            }

            override fun unregisterBreakpoint(breakpoint: XLineBreakpoint<FadeBasicBpProps>, delete: Boolean) {
                pushBreakpointsForFileFromIde(breakpoint)
            }
        }

    init {
        if (dapHandler.isStartNotified) {
            scheduleConnectDap()
        } else {
            dapHandler.addProcessListener(
                object : ProcessAdapter() {
                    override fun startNotified(event: ProcessEvent) {
                        dapHandler.removeProcessListener(this)
                        scheduleConnectDap()
                    }
                },
            )
        }
    }

    private fun scheduleConnectDap() {
        AppExecutorUtil.getAppExecutorService().execute {
            try {
                connectDap()
            } catch (e: Exception) {
                log.error("Fade DAP client failed", e)
                ApplicationManager.getApplication().invokeLater {
                    session.reportError("Fade DAP: ${e.message}")
                }
            }
        }
    }

    private fun connectDap() {
        val proc = getDapProcess(dapHandler)
        val launcher =
            DSPLauncher.createClientLauncher(
                dapClient,
                proc.inputStream,
                proc.outputStream,
            )
        launcherRef.set(launcher)
        val remote = launcher.remoteProxy
        remoteRef.set(remote)
        dapClient.remoteRef.set(remote)
        launcher.startListening()
        remote.initialize(FadeBasicDapLsp4jClient.buildInitializeArgs()).get()
        val programCsproj = Paths.get(configuration.resolvedProgramPath()).toAbsolutePath().normalize().toString()
        val args = LinkedHashMap<String, Any>()
        args["program"] = programCsproj
        if (sessionLogPaths.debuggerLogPath.isNotBlank()) {
            args["debuggerLogPath"] = sessionLogPaths.debuggerLogPath.trim()
        }
        if (sessionLogPaths.dapLogPath.isNotBlank()) {
            args["dapLogPath"] = sessionLogPaths.dapLogPath.trim()
        }
        when (configuration.mode) {
            FadeBasicDapRunConfiguration.Mode.ATTACH -> {
                // The DAP backend reads `debugPort` from configurationProperties as a string.
                args["debugPort"] = configuration.debugPort.toString()
                remote.attach(args).get()
            }
            FadeBasicDapRunConfiguration.Mode.LAUNCH -> {
                args["waitForDebugger"] = configuration.waitForDebugger
                remote.launch(args).get()
            }
        }
        if (sessionLogPaths.usedIdeDefaultFiles) {
            ApplicationManager.getApplication().invokeLater {
                val parts = mutableListOf<String>()
                parts += "Fade Basic session logs (folder: Help → Diagnostic Tools → Show Log in Explorer → fade-basic-dap)"
                if (sessionLogPaths.dapLogPath.isNotBlank()) {
                    parts += "DAP adapter: ${sessionLogPaths.dapLogPath}"
                }
                if (sessionLogPaths.debuggerLogPath.isNotBlank()) {
                    parts += "Debugger: ${sessionLogPaths.debuggerLogPath}"
                }
                session.reportMessage(parts.joinToString("\n"), MessageType.INFO)
            }
        }
    }

    private fun pushBreakpointsFromIde() {
        val remote = remoteRef.get() ?: return
        try {
            FadeBasicDapLsp4jClient.pushBreakpointsToAdapter(remote, configuration.project)
        } catch (e: Exception) {
            log.warn("setBreakpoints failed", e)
        }
    }

    private fun pushBreakpointsForFileFromIde(breakpoint: XLineBreakpoint<FadeBasicBpProps>) {
        val remote = remoteRef.get() ?: return
        val path = breakpoint.sourcePosition?.file?.path ?: return
        // Snapshot the enabled breakpoint lines for this file on the calling thread
        // (registerBreakpoint fires on EDT, where XBreakpointManager is safe to read).
        val lines = FadeBasicDapLsp4jClient.collectEnabledLinesForFile(configuration.project, path)
        // The DAP `.get()` blocks; do it on a background thread to keep EDT responsive.
        AppExecutorUtil.getAppExecutorService().execute {
            try {
                FadeBasicDapLsp4jClient.pushBreakpointsForFileLines(remote, path, lines)
            } catch (e: Exception) {
                log.warn("setBreakpoints (single file) failed", e)
            }
        }
    }

    private fun handleStopped(args: StoppedEventArguments) {
        val tid = args.threadId
        if (tid != null && tid > 0) {
            lastThreadId.set(tid)
        }
        val active = tid?.takeIf { it > 0 } ?: lastThreadId.get().takeIf { it > 0 }
        if (active == null) {
            log.warn("Stopped event without usable threadId")
            return
        }
        session.positionReached(DapSuspendContext(active))
    }

    private inner class DapSuspendContext(private val tid: Int) : XSuspendContext() {
        override fun getActiveExecutionStack(): XExecutionStack? =
            if (tid > 0) FadeBasicExecutionStack(tid) else null
    }

    private inner class FadeBasicExecutionStack(private val tid: Int) :
        XExecutionStack("Fade", null) {
        override fun getTopFrame(): XStackFrame? = null

        override fun computeStackFrames(firstFrameIndex: Int, container: XStackFrameContainer) {
            AppExecutorUtil.getAppExecutorService().execute {
                try {
                    val remote = remoteRef.get() ?: return@execute
                    val st =
                        remote
                            .stackTrace(
                                StackTraceArguments().apply {
                                    threadId = tid
                                    levels = 50
                                },
                            ).get()
                    val frames = st.stackFrames ?: emptyArray()
                    val mapped =
                        frames
                            .drop(firstFrameIndex)
                            .map { fr -> DapStackFrame(fr) }
                            .toList()
                    container.addStackFrames(mapped, true)
                } catch (e: Exception) {
                    log.warn("stackTrace failed", e)
                    container.errorOccurred(e.message ?: "stackTrace")
                }
            }
        }
    }

    private inner class DapStackFrame(private val frame: StackFrame) : XStackFrame() {
        override fun getSourcePosition(): XSourcePosition? {
            val path = frame.source?.path ?: return null
            // refreshAndFindFileByPath ensures the file is loaded into the VFS even if
            // it hasn't been opened in this session (e.g., stepping into a different .fbasic file)
            val vf = LocalFileSystem.getInstance().refreshAndFindFileByPath(path) ?: return null
            val line = if (frame.line > 0) frame.line - 1 else 0
            return XDebuggerUtil.getInstance().createPosition(vf, line)
        }

        override fun computeChildren(node: XCompositeNode) {
            AppExecutorUtil.getAppExecutorService().execute {
                try {
                    val remote = remoteRef.get() ?: return@execute
                    val sid = frame.id
                    val scopes = remote.scopes(ScopesArguments().apply { frameId = sid }).get().scopes ?: emptyArray()
                    if (scopes.isEmpty()) {
                        node.addChildren(XValueChildrenList.EMPTY, true)
                        return@execute
                    }
                    val list = XValueChildrenList()
                    for (scope in scopes) {
                        val scopeRef = scope.variablesReference
                        val vr =
                            remote
                                .variables(VariablesArguments().apply { variablesReference = scopeRef })
                                .get()
                        vr.variables?.forEach { v ->
                            list.add(v.name, dapVariable(v, parentRef = scopeRef))
                        }
                    }
                    node.addChildren(list, true)
                } catch (e: Exception) {
                    node.setErrorMessage(e.message ?: "variables")
                }
            }
        }

        override fun getEvaluator(): XDebuggerEvaluator = dapFrameEvaluator(frame.id)
    }

    private fun dapFrameEvaluator(frameId: Int): XDebuggerEvaluator =
        object : XDebuggerEvaluator() {
            override fun evaluate(expression: String, callback: XEvaluationCallback, position: XSourcePosition?) {
                AppExecutorUtil.getAppExecutorService().execute {
                    try {
                        val remote = remoteRef.get()
                        if (remote == null) {
                            callback.errorOccurred("No DAP session")
                            return@execute
                        }
                        val ctx =
                            if (position != null) {
                                EvaluateArgumentsContext.HOVER
                            } else {
                                EvaluateArgumentsContext.REPL
                            }
                        val res =
                            remote
                                .evaluate(
                                    EvaluateArguments().apply {
                                        this.expression = expression
                                        this.frameId = frameId
                                        context = ctx
                                    },
                                ).get()
                        callback.evaluated(dapEvalResult(res))
                    } catch (e: Exception) {
                        callback.errorOccurred(e.message ?: "evaluate")
                    }
                }
            }
        }

    override fun createConsole(): com.intellij.execution.ui.ExecutionConsole = debugConsole

    override fun sessionInitialized() {
        session.setPauseActionSupported(true)
    }

    override fun getEditorsProvider(): XDebuggerEditorsProvider = editorsProvider

    override fun doGetProcessHandler(): ProcessHandler = dapHandler

    @Suppress("UNCHECKED_CAST")
    override fun getBreakpointHandlers(): Array<XBreakpointHandler<*>> =
        arrayOf(breakpointHandler as XBreakpointHandler<*>)

    override fun resume(context: XSuspendContext?) {
        val tid = lastThreadId.get().takeIf { it > 0 } ?: return
        remoteRef.get()?.continue_(ContinueArguments().apply { threadId = tid })
    }

    override fun startPausing() {
        val tid = lastThreadId.get().takeIf { it > 0 } ?: return
        remoteRef.get()?.pause(PauseArguments().apply { threadId = tid })
    }

    override fun startStepOver(context: XSuspendContext?) {
        val tid = lastThreadId.get().takeIf { it > 0 } ?: return
        remoteRef.get()?.next(NextArguments().apply { threadId = tid })
    }

    override fun startStepInto(context: XSuspendContext?) {
        val tid = lastThreadId.get().takeIf { it > 0 } ?: return
        remoteRef.get()?.stepIn(StepInArguments().apply { threadId = tid })
    }

    override fun startStepOut(context: XSuspendContext?) {
        val tid = lastThreadId.get().takeIf { it > 0 } ?: return
        remoteRef.get()?.stepOut(StepOutArguments().apply { threadId = tid })
    }

    override fun getEvaluator(): XDebuggerEvaluator =
        object : XDebuggerEvaluator() {
            override fun evaluate(expression: String, callback: XEvaluationCallback, position: XSourcePosition?) {
                AppExecutorUtil.getAppExecutorService().execute {
                    try {
                        val remote = remoteRef.get()
                        if (remote == null) {
                            callback.errorOccurred("No DAP session")
                            return@execute
                        }
                        val threads = remote.threads().get().threads
                        val topFrameId =
                            threads
                                ?.firstOrNull()
                                ?.id
                                ?.let { tid ->
                                    remote
                                        .stackTrace(StackTraceArguments().apply { threadId = tid; levels = 1 })
                                        .get()
                                        .stackFrames
                                        ?.firstOrNull()
                                        ?.id
                                }
                        val res =
                            remote
                                .evaluate(
                                    EvaluateArguments().apply {
                                        this.expression = expression
                                        frameId = topFrameId
                                        context = EvaluateArgumentsContext.REPL
                                    },
                                ).get()
                        callback.evaluated(dapEvalResult(res))
                    } catch (e: Exception) {
                        callback.errorOccurred(e.message ?: "evaluate")
                    }
                }
            }
        }

    /** Creates an expandable XValue from a DAP evaluate response. */
    private fun dapEvalResult(res: org.eclipse.lsp4j.debug.EvaluateResponse): XValue {
        val varRef = res.variablesReference
        val hasChildren = varRef > 0
        return object : XValue() {
            override fun computePresentation(node: XValueNode, place: XValuePlace) {
                node.setPresentation(null, res.type.orEmpty(), res.result.orEmpty(), hasChildren)
            }

            override fun computeChildren(node: XCompositeNode) {
                if (!hasChildren) {
                    node.addChildren(XValueChildrenList.EMPTY, true)
                    return
                }
                AppExecutorUtil.getAppExecutorService().execute {
                    try {
                        val remote = remoteRef.get() ?: return@execute
                        val vr = remote.variables(VariablesArguments().apply { variablesReference = varRef }).get()
                        val list = XValueChildrenList()
                        vr.variables?.forEach { child ->
                            list.add(child.name, dapVariable(child, parentRef = varRef))
                        }
                        node.addChildren(list, true)
                    } catch (e: Exception) {
                        node.setErrorMessage(e.message ?: "variables")
                    }
                }
            }
        }
    }

    override fun stop() {
        // If the DAP adapter has already died (e.g., crashed with SIGABRT), there's no
        // point sending continue/disconnect — they'd just disappear into a closed pipe.
        val adapterAlive = (dapHandler as? DapProcessHandler)?.isProcessAlive() ?: true
        if (!adapterAlive) {
            log.warn("Fade DAP: stop() called but adapter is already dead; skipping continue+disconnect")
            dapHandler.destroyProcess()
            return
        }
        // CRITICAL: do all DAP I/O off EDT. `remote.continue_()`/`remote.disconnect()` write
        // to the adapter's stdin via lsp4j; if the adapter is busy (e.g., handling a
        // dropped-debuggee event), the write blocks until the adapter reads its pipe. On EDT
        // that blocks the whole IDE — which manifests as "Rider hangs on first breakpoint
        // after reconnect", because session-end runs through stop() too.
        val mode = configuration.mode
        val tid = lastThreadId.get()
        val remote = remoteRef.get()
        AppExecutorUtil.getAppExecutorService().execute {
            // In attach mode, the DAP backend doesn't auto-resume the runtime on disconnect.
            // If we're paused at a breakpoint and the user clicks Stop, the adapter dies but
            // the game is left frozen on its breakpoint forever. Send `continue` first so the
            // runtime resumes, THEN send disconnect.
            if (mode == FadeBasicDapRunConfiguration.Mode.ATTACH && tid > 0) {
                try {
                    log.info("Fade DAP: resuming debuggee (thread=$tid) before disconnect (attach mode)")
                    remote?.continue_(ContinueArguments().apply { threadId = tid })
                } catch (e: Exception) {
                    log.warn("Fade DAP: pre-disconnect continue failed", e)
                }
            }
            // Send disconnect (fire-and-forget — don't await the response). In attach mode,
            // explicitly ask the adapter NOT to terminate the externally-running debuggee.
            try {
                val args = DisconnectArguments().apply {
                    if (mode == FadeBasicDapRunConfiguration.Mode.ATTACH) {
                        terminateDebuggee = false
                    }
                }
                remote?.disconnect(args)
            } catch (_: Exception) {
            }
            // Sequence inside the same background task so the disconnect bytes have a chance
            // to reach the adapter before we SIGTERM it. Doing this on EDT after the async
            // submit caused a race that sometimes killed the adapter mid-write.
            try {
                val debuggee = dapClient.debuggeeHandlerRef.getAndSet(null)
                if (debuggee is com.intellij.execution.process.KillableProcessHandler) {
                    debuggee.killProcess()
                } else {
                    debuggee?.destroyProcess()
                }
            } catch (_: Exception) {
            }
            dapHandler.destroyProcess()
        }
    }

    /** Creates an XValue for a DAP variable. If the variable has children (struct fields or array
     *  elements), the node is expandable -- clicking it fetches children via the variables request.
     *  [parentRef] is the variablesReference of the parent scope/struct so setVariable can find this var. */
    private fun dapVariable(v: org.eclipse.lsp4j.debug.Variable, parentRef: Int = 0): XValue {
        val varRef = v.variablesReference
        val hasChildren = varRef > 0
        return object : XValue() {
            override fun computePresentation(node: XValueNode, place: XValuePlace) {
                node.setPresentation(null, v.type.orEmpty(), v.value.orEmpty(), hasChildren)
            }

            override fun getModifier(): com.intellij.xdebugger.frame.XValueModifier? {
                if (parentRef <= 0) return null
                return object : com.intellij.xdebugger.frame.XValueModifier() {
                    override fun getInitialValueEditorText(): String = v.value.orEmpty()
                    override fun setValue(newValue: String, callback: XModificationCallback) {
                        AppExecutorUtil.getAppExecutorService().execute {
                            try {
                                val remote = remoteRef.get()
                                if (remote == null) {
                                    callback.errorOccurred("No DAP session")
                                    return@execute
                                }
                                val resp = remote.setVariable(
                                    org.eclipse.lsp4j.debug.SetVariableArguments().apply {
                                        variablesReference = parentRef
                                        name = v.name
                                        value = newValue
                                    }
                                ).get()
                                v.value = resp.value
                                v.type = resp.type
                                callback.valueModified()
                                // Force the Variables view to re-fetch — sibling/child values
                                // may also have changed (e.g., aliased fields, computed props).
                                ApplicationManager.getApplication().invokeLater { session.rebuildViews() }
                            } catch (e: Exception) {
                                callback.errorOccurred(e.message ?: "setVariable failed")
                            }
                        }
                    }
                }
            }

            override fun computeChildren(node: XCompositeNode) {
                if (!hasChildren) {
                    node.addChildren(XValueChildrenList.EMPTY, true)
                    return
                }
                AppExecutorUtil.getAppExecutorService().execute {
                    try {
                        val remote = remoteRef.get() ?: return@execute
                        val vr = remote.variables(VariablesArguments().apply { variablesReference = varRef }).get()
                        val list = XValueChildrenList()
                        vr.variables?.forEach { child ->
                            list.add(child.name, dapVariable(child, parentRef = varRef))
                        }
                        node.addChildren(list, true)
                    } catch (e: Exception) {
                        node.setErrorMessage(e.message ?: "variables")
                    }
                }
            }
        }
    }

    companion object {
        private fun getDapProcess(handler: ProcessHandler): Process {
            if (handler is DapProcessHandler) return handler.javaProcess()
            if (handler is BaseProcessHandler<*>) return handler.process
            error("DAP handler must expose a Java Process")
        }

        /** Map a [com.intellij.openapi.util.Key] from a [com.intellij.execution.process.ProcessHandler]
         *  (typically [com.intellij.execution.process.ProcessOutputType]) to a console content type. */
        private fun consoleTypeForProcessOutput(
            key: com.intellij.openapi.util.Key<*>,
        ): com.intellij.execution.ui.ConsoleViewContentType {
            return when (key) {
                com.intellij.execution.process.ProcessOutputType.STDERR ->
                    com.intellij.execution.ui.ConsoleViewContentType.ERROR_OUTPUT
                com.intellij.execution.process.ProcessOutputType.SYSTEM ->
                    com.intellij.execution.ui.ConsoleViewContentType.SYSTEM_OUTPUT
                else -> com.intellij.execution.ui.ConsoleViewContentType.NORMAL_OUTPUT
            }
        }

        /** Map a DAP `output` event category string to a console content type. */
        private fun consoleTypeForDapCategory(
            category: String?,
        ): com.intellij.execution.ui.ConsoleViewContentType {
            return when (category) {
                "stderr" -> com.intellij.execution.ui.ConsoleViewContentType.ERROR_OUTPUT
                "important" -> com.intellij.execution.ui.ConsoleViewContentType.ERROR_OUTPUT
                "console" -> com.intellij.execution.ui.ConsoleViewContentType.SYSTEM_OUTPUT
                "telemetry" -> com.intellij.execution.ui.ConsoleViewContentType.LOG_DEBUG_OUTPUT
                else -> com.intellij.execution.ui.ConsoleViewContentType.NORMAL_OUTPUT
            }
        }

        fun stringResultValue(text: String?): XValue =
            object : XValue() {
                override fun computePresentation(node: XValueNode, place: XValuePlace) {
                    node.setPresentation(null, XStringValuePresentation(text.orEmpty()), false)
                }
            }
    }
}

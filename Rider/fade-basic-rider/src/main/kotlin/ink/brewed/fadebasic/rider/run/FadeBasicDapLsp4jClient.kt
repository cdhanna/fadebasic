package ink.brewed.fadebasic.rider.run

import com.intellij.execution.configurations.GeneralCommandLine
import com.intellij.execution.process.KillableColoredProcessHandler
import com.intellij.execution.process.ProcessAdapter
import com.intellij.execution.process.ProcessEvent
import com.intellij.openapi.application.ApplicationManager
import com.intellij.openapi.diagnostic.logger
import com.intellij.openapi.project.Project
import com.intellij.openapi.util.Key
import com.intellij.util.concurrency.AppExecutorUtil
import com.intellij.xdebugger.XDebuggerManager
import com.intellij.xdebugger.breakpoints.XBreakpointManager
import com.intellij.xdebugger.breakpoints.XLineBreakpoint
import ink.brewed.fadebasic.rider.debug.FadeBasicBpProps
import ink.brewed.fadebasic.rider.debug.FadeBasicLineBreakpointType
import org.eclipse.lsp4j.debug.ConfigurationDoneArguments
import org.eclipse.lsp4j.debug.InitializeRequestArguments
import org.eclipse.lsp4j.debug.InitializeRequestArgumentsPathFormat
import org.eclipse.lsp4j.debug.RunInTerminalRequestArguments
import org.eclipse.lsp4j.debug.RunInTerminalResponse
import org.eclipse.lsp4j.debug.Source
import org.eclipse.lsp4j.debug.SourceBreakpoint
import org.eclipse.lsp4j.debug.StoppedEventArguments
import org.eclipse.lsp4j.debug.SetBreakpointsArguments
import org.eclipse.lsp4j.debug.services.IDebugProtocolClient
import org.eclipse.lsp4j.debug.services.IDebugProtocolServer
import java.util.concurrent.CompletableFuture
import java.util.concurrent.atomic.AtomicReference

/**
 * DAP **client** side (IDE → adapter) for the Fade Basic debug adapter. Implements [runInTerminal] because the
 * adapter launches the debuggee via that request when [supportsRunInTerminalRequest] is enabled in [initialize].
 */
class FadeBasicDapLsp4jClient(
    private val project: Project,
    private val onStopped: (StoppedEventArguments) -> Unit,
    private val onTerminated: () -> Unit,
    private val onDebuggeeOutput: ((String, Key<*>) -> Unit)? = null,
    /** Called when the DAP adapter itself emits an `output` event. The `category` is the
     *  DAP-spec category string (`stdout`, `stderr`, `console`, `important`, `telemetry`, …). */
    private val onAdapterOutput: ((String, String?) -> Unit)? = null,
) : IDebugProtocolClient {

    private val log = logger<FadeBasicDapLsp4jClient>()
    val remoteRef = AtomicReference<IDebugProtocolServer?>(null)
    val debuggeeHandlerRef = AtomicReference<com.intellij.execution.process.ProcessHandler?>(null)

    /** Kill the debuggee process if it's still running. */
    fun destroyDebuggee() {
        debuggeeHandlerRef.getAndSet(null)?.destroyProcess()
    }

    override fun runInTerminal(args: RunInTerminalRequestArguments): CompletableFuture<RunInTerminalResponse> =
        CompletableFuture.supplyAsync(
            {
                val argv = args.args
                if (argv.isEmpty()) error("runInTerminal: no argv")
                log.info("Fade DAP runInTerminal cwd=${args.cwd} argv=${argv.joinToString(" ")}")
                log.info("Fade DAP runInTerminal env=${args.env?.entries?.joinToString(", ") { "${it.key}=${it.value}" }}")
                val line = GeneralCommandLine().withExePath(argv[0]).withParameters(argv.drop(1))
                line.withParentEnvironmentType(GeneralCommandLine.ParentEnvironmentType.CONSOLE)
                args.cwd?.takeIf { it.isNotBlank() }?.let { line.workDirectory = java.io.File(it) }
                args.env?.forEach { (k, v) -> line.environment[k] = v?.toString() ?: "" }
                try {
                    // Use KillableColoredProcessHandler to capture the debuggee's stdout/stderr
                    // and relay it to the debug console. This is the DEBUGGEE process (not the
                    // DAP adapter), so reading its stdout is correct.
                    val handler = KillableColoredProcessHandler(line)
                    debuggeeHandlerRef.set(handler)
                    if (onDebuggeeOutput != null) {
                        handler.addProcessListener(object : ProcessAdapter() {
                            override fun onTextAvailable(event: ProcessEvent, outputType: Key<*>) {
                                onDebuggeeOutput.invoke(event.text, outputType)
                            }
                        })
                    }
                    handler.startNotify()
                    val pid = handler.process.pid()
                    log.info("Fade DAP runInTerminal started pid=$pid")
                    RunInTerminalResponse().apply { processId = pid.toInt() }
                } catch (e: Exception) {
                    log.error("Fade DAP runInTerminal failed to start process", e)
                    throw e
                }
            },
            AppExecutorUtil.getAppExecutorService(),
        )

    override fun initialized() {
        val remote = remoteRef.get() ?: return
        // Snapshot breakpoint state on EDT (XBreakpointManager requires it), then send
        // every DAP request on a background thread. Doing the .get() on EDT will deadlock
        // the entire IDE if the adapter is wedged (e.g., its runtime connection dropped
        // mid-handshake and setBreakpoints can never be answered).
        ApplicationManager.getApplication().invokeLater {
            val perFile = snapshotBreakpointsByFile(project)
            AppExecutorUtil.getAppExecutorService().execute {
                for ((path, lines) in perFile) {
                    try {
                        // Bound the wait so a wedged adapter can't pin this thread forever.
                        // The reader thread will still deliver the eventual response (if any)
                        // back into the lsp4j request map; we just stop waiting on it.
                        remote.setBreakpoints(makeSetBreakpointsArgs(path, lines))
                            .get(3, java.util.concurrent.TimeUnit.SECONDS)
                    } catch (e: Exception) {
                        log.warn("Fade DAP: setBreakpoints for $path failed/timeout", e)
                    }
                }
                try {
                    remote.configurationDone(ConfigurationDoneArguments())
                        .get(3, java.util.concurrent.TimeUnit.SECONDS)
                } catch (e: Exception) {
                    log.warn("Fade DAP: configurationDone failed/timeout", e)
                }
            }
        }
    }

    override fun stopped(args: StoppedEventArguments) {
        onStopped(args)
    }

    override fun output(args: org.eclipse.lsp4j.debug.OutputEventArguments) {
        val text = args.output ?: return
        onAdapterOutput?.invoke(text, args.category)
    }

    override fun terminated(args: org.eclipse.lsp4j.debug.TerminatedEventArguments?) {
        log.warn("Fade DAP: 'terminated' event received from adapter; ending debug session")
        onTerminated()
    }

    override fun exited(args: org.eclipse.lsp4j.debug.ExitedEventArguments?) {
        val code = args?.exitCode
        log.warn("Fade DAP: 'exited' event received from adapter (exitCode=$code); ending debug session")
        onTerminated()
    }

    companion object {
        /** Sends [SetBreakpoints] for all enabled Fade Basic line breakpoints (DAP lines are 1-based).
         *  IMPORTANT: this BLOCKS the calling thread on the DAP response. Never invoke from EDT —
         *  if the adapter is wedged the IDE will hang. Use [snapshotBreakpointsByFile] on EDT, then
         *  send on a background thread instead. */
        fun pushBreakpointsToAdapter(remote: IDebugProtocolServer, project: Project) {
            pushBreakpoints(remote, XDebuggerManager.getInstance(project).breakpointManager)
        }

        /** Read every enabled Fade Basic line breakpoint and group by source path.
         *  Caller is responsible for invoking on EDT. Returns DAP-1-based line numbers. */
        fun snapshotBreakpointsByFile(project: Project): Map<String, IntArray> {
            val mgr = XDebuggerManager.getInstance(project).breakpointManager
            @Suppress("UNCHECKED_CAST")
            val raw =
                mgr.getBreakpoints(FadeBasicLineBreakpointType::class.java) as Collection<XLineBreakpoint<FadeBasicBpProps>>
            val byPath = LinkedHashMap<String, MutableList<Int>>()
            for (bp in raw) {
                if (!bp.isEnabled) continue
                val path = bp.sourcePosition?.file?.path ?: continue
                byPath.getOrPut(path) { mutableListOf() }.add(bp.line + 1)
            }
            return byPath.mapValues { it.value.toIntArray() }
        }

        /** Build a [SetBreakpointsArguments] for [filePath] with [lines] (DAP-1-based). */
        fun makeSetBreakpointsArgs(filePath: String, lines: IntArray): SetBreakpointsArguments {
            val bps = lines.map { l -> SourceBreakpoint().apply { line = l } }.toTypedArray()
            return SetBreakpointsArguments().apply {
                source = Source().apply { this.path = filePath }
                breakpoints = bps
            }
        }

        /** Read the enabled DAP-1-based line numbers for [filePath] from the IDE breakpoint manager.
         *  Caller is responsible for invoking on EDT. */
        fun collectEnabledLinesForFile(project: Project, filePath: String): IntArray {
            val mgr = XDebuggerManager.getInstance(project).breakpointManager
            @Suppress("UNCHECKED_CAST")
            val raw =
                mgr.getBreakpoints(FadeBasicLineBreakpointType::class.java) as Collection<XLineBreakpoint<FadeBasicBpProps>>
            return raw
                .filter { it.isEnabled && it.sourcePosition?.file?.path == filePath }
                .map { it.line + 1 }
                .toIntArray()
        }

        /** Send setBreakpoints for [filePath] with [lines] (DAP-1-based). Empty [lines] clears the file
         *  per DAP spec. Used after add/remove of one breakpoint so we don't re-send unrelated files.
         *  Bounded by a timeout — a wedged adapter must not pin the caller's thread forever. */
        fun pushBreakpointsForFileLines(remote: IDebugProtocolServer, filePath: String, lines: IntArray) {
            remote.setBreakpoints(makeSetBreakpointsArgs(filePath, lines))
                .get(3, java.util.concurrent.TimeUnit.SECONDS)
        }

        fun buildInitializeArgs(): InitializeRequestArguments =
            InitializeRequestArguments().apply {
                clientID = "rider-fade-basic"
                clientName = "JetBrains Rider"
                adapterID = "fade-basic"
                linesStartAt1 = true
                columnsStartAt1 = true
                pathFormat = InitializeRequestArgumentsPathFormat.PATH
                supportsRunInTerminalRequest = true
            }

        private fun pushBreakpoints(remote: IDebugProtocolServer, mgr: XBreakpointManager) {
            @Suppress("UNCHECKED_CAST")
            val raw =
                mgr.getBreakpoints(FadeBasicLineBreakpointType::class.java) as Collection<XLineBreakpoint<FadeBasicBpProps>>
            val byPath =
                raw.filter { it.isEnabled }.mapNotNull { bp ->
                    val path = bp.sourcePosition?.file?.path ?: return@mapNotNull null
                    path to bp
                }.groupBy({ it.first }, { it.second })
            for ((path, list) in byPath) {
                val src = Source().apply { this.path = path }
                val bps =
                    list.map { bp ->
                        SourceBreakpoint().apply {
                            line = bp.line + 1
                        }
                    }.toTypedArray()
                val args = SetBreakpointsArguments().apply {
                    source = src
                    breakpoints = bps
                }
                remote.setBreakpoints(args).get()
            }
        }
    }
}

package ink.brewed.fadebasic.rider.run

import com.intellij.openapi.diagnostic.logger
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.nio.charset.StandardCharsets

/**
 * UDP discovery for running Fade Basic debug servers. The runtime broadcasts itself when it
 * receives the magic message "FADE_DEBUG_DISCOVERY" on port 21758, replying with a JSON blob
 * describing the listening process. See [DebugSession.cs] in FadeBasic/Launch.
 */
object FadeBasicDapDebugServerDiscovery {
    private const val DISCOVERY_PORT = 21758
    private const val DISCOVERY_MESSAGE = "FADE_DEBUG_DISCOVERY"
    private val log = logger<FadeBasicDapDebugServerDiscovery>()

    /** A minimally-parsed reply from a running debug server. */
    data class Server(
        val port: Int,
        val processId: Int,
        val label: String,
        val processName: String,
        val processWindowTitle: String,
        /** LAN IP address the discovery reply originated from. */
        val host: String,
    ) {
        /** Short single-line label for plain string pickers (`Messages.showChooseDialog`). */
        fun displayLabel(): String {
            val title = processWindowTitle.takeIf { it.isNotBlank() } ?: processName.ifBlank { "fade" }
            val tag = label.takeIf { it.isNotBlank() }
            val pid = if (processId > 0) "pid $processId" else null
            val suffix = listOfNotNull(tag, pid).joinToString(" · ")
            return if (suffix.isNotBlank()) "$title  ($suffix)  → $host:$port" else "$title  → $host:$port"
        }

        /** Headline for richer renderers — process window title or process name. */
        fun primaryTitle(): String =
            processWindowTitle.takeIf { it.isNotBlank() }
                ?: processName.takeIf { it.isNotBlank() }
                ?: "fade"
    }

    /** Send one broadcast and collect replies for [timeoutMs]. Blocking; call from a background thread. */
    fun discover(timeoutMs: Int = 600): List<Server> {
        val results = mutableListOf<Server>()
        DatagramSocket().use { socket ->
            socket.broadcast = true
            socket.soTimeout = timeoutMs
            val payload = DISCOVERY_MESSAGE.toByteArray(StandardCharsets.UTF_8)
            val out = DatagramPacket(
                payload, payload.size,
                InetAddress.getByName("255.255.255.255"), DISCOVERY_PORT,
            )
            try {
                socket.send(out)
            } catch (e: Exception) {
                log.warn("Fade discovery: send failed", e)
                return emptyList()
            }
            val deadline = System.currentTimeMillis() + timeoutMs
            val buf = ByteArray(8 * 1024)
            while (System.currentTimeMillis() < deadline) {
                try {
                    val packet = DatagramPacket(buf, buf.size)
                    socket.receive(packet)
                    val text = String(packet.data, packet.offset, packet.length, StandardCharsets.UTF_8)
                    val sourceHost = packet.address?.hostAddress.orEmpty()
                    parseServer(text, sourceHost)?.let { results += it }
                } catch (_: java.net.SocketTimeoutException) {
                    break
                } catch (e: Exception) {
                    log.warn("Fade discovery: receive failed", e)
                    break
                }
            }
        }
        return results.distinctBy { it.port to it.processId }
    }

    /**
     * Tiny JSON-string and JSON-int extractor. The server's payload uses field names
     * `port`, `processId`, `label`, `processName`, `processWindowTitle` and emits flat top-level
     * primitives only — a regex-grade parse is sufficient and avoids pulling in a JSON library
     * just for this utility.
     */
    private fun parseServer(json: String, host: String): Server? {
        val port = readInt(json, "port") ?: return null
        return Server(
            port = port,
            processId = readInt(json, "processId") ?: 0,
            label = readString(json, "label") ?: "",
            processName = readString(json, "processName") ?: "",
            processWindowTitle = readString(json, "processWindowTitle") ?: "",
            host = host,
        )
    }

    private fun readString(json: String, field: String): String? {
        val rx = Regex("\"" + Regex.escape(field) + "\"\\s*:\\s*\"((?:\\\\.|[^\"\\\\])*)\"")
        val m = rx.find(json) ?: return null
        return m.groupValues[1]
            .replace("\\\"", "\"")
            .replace("\\\\", "\\")
            .replace("\\n", "\n")
            .replace("\\r", "\r")
            .replace("\\t", "\t")
    }

    private fun readInt(json: String, field: String): Int? {
        val rx = Regex("\"" + Regex.escape(field) + "\"\\s*:\\s*(-?\\d+)")
        return rx.find(json)?.groupValues?.get(1)?.toIntOrNull()
    }
}

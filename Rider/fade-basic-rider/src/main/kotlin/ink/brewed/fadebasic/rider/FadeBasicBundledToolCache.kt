package ink.brewed.fadebasic.rider

import com.intellij.openapi.application.PathManager
import com.intellij.openapi.diagnostic.logger
import java.net.JarURLConnection
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption

object FadeBasicBundledToolCache {
    private val log = logger<FadeBasicBundledToolCache>()

    // Where the whole bundled `tools/` tree gets materialised, once per session.
    // null until the first successful extraction (or an on-disk dir in dev).
    @Volatile
    private var toolsDir: Path? = null

    /** Absolute path to a bundled tool entrypoint (e.g. "LSP.dll" or "DAP.dll"),
     *  with all of its runtime companions (LSP.runtimeconfig.json, LSP.deps.json
     *  and every dependency assembly) sitting beside it. Returns null when the
     *  tools aren't bundled in this build.
     *
     *  The whole `tools/` directory must be extracted together: `dotnet LSP.dll`
     *  reads LSP.runtimeconfig.json to pick the shared framework, and without it
     *  the host treats the app as self-contained and fails looking for
     *  libhostpolicy.dylib. Extracting only LSP.dll (the old behaviour) is what
     *  broke framework-dependent startup. */
    fun bundledDllPath(classLoader: ClassLoader?, resourceFileName: String): Path? {
        if (classLoader == null) return null
        val dir = ensureToolsExtracted(classLoader) ?: return null
        val target = dir.resolve(resourceFileName)
        return if (Files.isRegularFile(target)) target else null
    }

    private fun ensureToolsExtracted(classLoader: ClassLoader): Path? {
        toolsDir?.let { if (Files.isDirectory(it)) return it }
        synchronized(this) {
            toolsDir?.let { if (Files.isDirectory(it)) return it }
            val dir = extractToolsTree(classLoader)
            toolsDir = dir
            return dir
        }
    }

    /** Materialise every resource under `tools/` so the entry dll has its full
     *  framework-dependent deployment on disk. Handles both packagings:
     *   - jar (installed plugin): walk the jar's `tools/` entries and copy each
     *     out to a temp dir, preserving any sub-paths (e.g. `runtimes/`).
     *   - file (dev / sandbox with exploded resources): the `tools/` dir already
     *     exists on disk, so use it in place — no copy needed. */
    private fun extractToolsTree(classLoader: ClassLoader): Path? {
        // Anchor on a file we know is there; the URL tells us the packaging and,
        // for jars, which jar to walk.
        val anchor = classLoader.getResource("tools/LSP.dll")
            ?: classLoader.getResource("tools/DAP.dll")
            ?: return null

        if (anchor.protocol == "file") {
            return runCatching { Path.of(anchor.toURI()).parent }
                .onFailure { log.warn("Failed to resolve on-disk bundled tools dir", it) }
                .getOrNull()
        }

        return try {
            val conn = anchor.openConnection()
            if (conn !is JarURLConnection) {
                log.warn("Bundled tools resource is neither file nor jar: $anchor")
                return null
            }
            val dest = Path.of(PathManager.getTempPath(), "fade-basic-rider-tools")
            Files.createDirectories(dest)
            // JarURLConnection caches the JarFile; don't close it (the platform
            // and future lookups reuse it).
            val jar = conn.jarFile
            val entries = jar.entries()
            var copied = 0
            while (entries.hasMoreElements()) {
                val entry = entries.nextElement()
                if (entry.isDirectory) continue
                val name = entry.name
                if (!name.startsWith("tools/")) continue
                val rel = name.removePrefix("tools/")
                if (rel.isEmpty()) continue
                val target = dest.resolve(rel).normalize()
                // Zip-slip guard: never write outside dest.
                if (!target.startsWith(dest)) {
                    log.warn("Skipping bundled tool entry outside dest: $name")
                    continue
                }
                target.parent?.let { Files.createDirectories(it) }
                jar.getInputStream(entry).use { input ->
                    Files.copy(input, target, StandardCopyOption.REPLACE_EXISTING)
                }
                copied++
            }
            if (copied == 0) {
                log.warn("No bundled tool files found under tools/ in $anchor")
                null
            } else {
                dest
            }
        } catch (e: Exception) {
            log.warn("Failed to extract bundled tools tree", e)
            null
        }
    }
}

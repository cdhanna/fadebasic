package ink.brewed.fadebasic.rider

import com.intellij.openapi.application.PathManager
import com.intellij.openapi.diagnostic.logger
import java.io.InputStream
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.StandardCopyOption
import java.util.concurrent.ConcurrentHashMap

object FadeBasicBundledToolCache {
    private val log = logger<FadeBasicBundledToolCache>()
    private val extracted = ConcurrentHashMap<String, Path>()

    fun bundledDllPath(classLoader: ClassLoader?, resourceFileName: String): Path? {
        if (classLoader == null) return null
        extracted[resourceFileName]?.let { cached ->
            if (Files.isRegularFile(cached)) return cached
        }
        val stream = classLoader.getResourceAsStream("tools/$resourceFileName") ?: return null
        return extractLocked(resourceFileName, stream)
    }

    private fun extractLocked(name: String, stream: InputStream): Path? {
        synchronized(FadeBasicBundledToolCache) {
            extracted[name]?.let { cached ->
                if (Files.isRegularFile(cached)) return cached
            }
            return try {
                stream.use { input ->
                    val dir = Path.of(PathManager.getTempPath(), "fade-basic-rider-tools")
                    Files.createDirectories(dir)
                    val target = dir.resolve(name)
                    Files.copy(input, target, StandardCopyOption.REPLACE_EXISTING)
                    extracted[name] = target
                    target
                }
            } catch (e: Exception) {
                log.warn("Failed to extract bundled tool $name", e)
                null
            }
        }
    }
}

package ink.brewed.fadebasic.rider.run

import com.intellij.openapi.project.Project
import java.nio.file.FileVisitResult
import java.nio.file.Files
import java.nio.file.Path
import java.nio.file.SimpleFileVisitor
import java.nio.file.attribute.BasicFileAttributes
import kotlin.io.path.extension

object FadeBasicDapProgramDiscoverer {

    private const val MAX_DEPTH = 14
    private const val MAX_FILES = 400

    fun discoverCsprojPaths(project: Project): List<String> = discoverFromDir(project.basePath)

    fun defaultProgramOrEmpty(project: Project): String = discoverCsprojPaths(project).singleOrNull() ?: ""

    fun discoverFromDir(basePath: String?): List<String> {
        if (basePath.isNullOrBlank()) return emptyList()
        val root = Path.of(basePath)
        if (!Files.isDirectory(root)) return emptyList()
        val found = linkedSetOf<String>()
        var fileCount = 0
        Files.walkFileTree(
            root,
            object : SimpleFileVisitor<Path>() {
                override fun preVisitDirectory(dir: Path, attrs: BasicFileAttributes): FileVisitResult {
                    val depth = root.relativize(dir).nameCount
                    if (depth > MAX_DEPTH) return FileVisitResult.SKIP_SUBTREE
                    val name = dir.fileName?.toString().orEmpty()
                    if (name == "node_modules" || name == ".git" || name == "build" || name == "bin" || name == "obj") {
                        return FileVisitResult.SKIP_SUBTREE
                    }
                    return FileVisitResult.CONTINUE
                }

                override fun visitFile(file: Path, attrs: BasicFileAttributes): FileVisitResult {
                    if (!attrs.isRegularFile) return FileVisitResult.CONTINUE
                    if (file.extension.equals("csproj", ignoreCase = true)) {
                        found.add(file.toAbsolutePath().normalize().toString())
                        fileCount++
                        if (fileCount >= MAX_FILES) return FileVisitResult.TERMINATE
                    }
                    return FileVisitResult.CONTINUE
                }
            },
        )
        return found.sorted().toList()
    }
}

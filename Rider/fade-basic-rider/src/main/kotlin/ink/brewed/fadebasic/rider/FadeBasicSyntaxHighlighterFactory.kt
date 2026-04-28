package ink.brewed.fadebasic.rider

import com.intellij.openapi.fileTypes.PlainTextLanguage
import com.intellij.openapi.fileTypes.SyntaxHighlighter
import com.intellij.openapi.fileTypes.SyntaxHighlighterFactory
import com.intellij.openapi.project.Project
import com.intellij.openapi.vfs.VirtualFile

class FadeBasicSyntaxHighlighterFactory : SyntaxHighlighterFactory() {
    override fun getSyntaxHighlighter(project: Project?, file: VirtualFile?): SyntaxHighlighter =
        SyntaxHighlighterFactory.getSyntaxHighlighter(PlainTextLanguage.INSTANCE, project, file)
            ?: error("Plain text highlighter must exist")
}

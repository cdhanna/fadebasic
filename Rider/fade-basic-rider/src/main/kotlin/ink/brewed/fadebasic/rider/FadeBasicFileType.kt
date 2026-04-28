package ink.brewed.fadebasic.rider

import com.intellij.icons.AllIcons
import com.intellij.openapi.fileTypes.LanguageFileType
import com.intellij.openapi.vfs.VirtualFile
import java.util.Locale

class FadeBasicFileType private constructor() : LanguageFileType(FadeBasicLanguage) {

    override fun getName(): String = "Fade Basic"

    override fun getDescription(): String = "Fade Basic source"

    override fun getDefaultExtension(): String = "fbasic"

    override fun getIcon() = AllIcons.FileTypes.Any_type

    companion object {
        @JvmField
        val INSTANCE = FadeBasicFileType()

        fun isFadeBasic(file: VirtualFile): Boolean {
            val ext = file.extension?.lowercase(Locale.ROOT) ?: return false
            return ext == "fbasic" || ext == "fb"
        }
    }
}

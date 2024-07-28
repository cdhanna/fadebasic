namespace FadeBasic.Util;

public static class PathUtil
{
    
    public static bool TryGetProjectPath(string inputPath, out string path)
    {
        path = null;
        if(File.Exists(inputPath))
        {
            // is file
            path = Path.GetFullPath(inputPath);
            return true;
        }
        else if(Directory.Exists(inputPath))
        {
            // is Folder 
            var subFiles = Directory.GetFiles(inputPath, $"*{FadeBasicConstants.FadeBasicProjectExt}", SearchOption.TopDirectoryOnly);
            if (subFiles.Length == 1)
            {
                path = Path.GetFullPath(subFiles[0]);
                return true;
            }
            else
            {
                return false;
            }
        }
        else
        {
            // invalid path
            return false;
        }
    }
}
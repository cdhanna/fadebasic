using System.Collections.Generic;

namespace DarkBasicYo.ApplicationSupport.Project
{
    public class ProjectContext
    {
        public string name;
        public string absoluteContextDir;

        public string targetFramework;

        /// <summary>
        /// a project must manually specify a list of source files.
        /// The order of the list is important, as the final compiled program will be the concatenation of all files.
        /// </summary>
        public HashSet<string> absoluteSourceFiles;

        /// <summary>
        /// project command sources define a local collection of .csproj files
        /// that have <see cref="DarkBasicYo.SourceGenerators.DarkBasicCommandAttribute"/> attributes available
        /// </summary>
        public List<ProjectCommandSource> projectLibraries;
        
        /// <summary>
        /// list of built in libraries. These version numbers will match the installed
        /// project version, because they are the same?
        /// </summary>
        public List<string> standardLibraries;
        
    }

    public class ProjectCommandSource
    {
        /// <summary>
        /// the absolute path to the .csproj
        /// </summary>
        public string absoluteProjectPath;
        
        /// <summary>
        /// the list of class names to read for commands
        /// </summary>
        public List<string> commandClasses = new List<string>();

        /// <summary>
        /// the absolute path to the built .dll file
        /// </summary>
        public string absoluteOutputDllPath;

        /// <summary>
        /// true when the <see cref="absoluteOutputDllPath"/> points to a valid file.
        /// </summary>
        public bool hasBuiltDll;
    }
    
}
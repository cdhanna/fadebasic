using System.Collections.Generic;

namespace FadeBasic.ApplicationSupport.Project
{
    public class ProjectContext
    {

        /// <summary>
        /// a project must manually specify a list of source files.
        /// The order of the list is important, as the final compiled program will be the concatenation of all files.
        /// </summary>
        public List<string> absoluteSourceFiles = new List<string>();

        /// <summary>
        /// project command sources define a local collection of .csproj files
        /// that have <see cref="FadeBasic.SourceGenerators.FadeBasicCommandAttribute"/> attributes available
        /// </summary>
        public List<ProjectCommandSource> projectLibraries = new List<ProjectCommandSource>();
        
        
    }

    public class ProjectCommandSource
    {
        /// <summary>
        /// the list of class names to read for commands
        /// </summary>
        public List<string> commandClasses = new List<string>();

        /// <summary>
        /// the absolute path to the built .dll file
        /// </summary>
        public string absoluteOutputDllPath;
    }
    
}
using System.Collections.Generic;

namespace FadeBasic.ApplicationSupport.Project
{
    public class ProjectContext
    {
        public string name;
        public string absoluteContextDir;

        public string targetFramework;

        /// <summary>
        /// a project must specify a .csproj path that will be used to launch the program.
        /// When the the project is launched,
        ///  the basic-project will be compiled, and
        ///  the byte-code will be saved inside a class-file relative to the launch csproj,
        ///  COMPILED_BASIC_SCRIPT.cs
        ///
        ///  then the csproj will be launched, and the expectation is that the program
        ///  will boot the byte-code stored in COMPILED_BASIC_SCRIPT.byteCode
        ///
        ///  
        /// </summary>
        public string absoluteLaunchCsProjPath;

        /// <summary>
        /// a project must manually specify a list of source files.
        /// The order of the list is important, as the final compiled program will be the concatenation of all files.
        /// </summary>
        public List<string> absoluteSourceFiles;

        /// <summary>
        /// project command sources define a local collection of .csproj files
        /// that have <see cref="FadeBasic.SourceGenerators.FadeBasicCommandAttribute"/> attributes available
        /// </summary>
        public List<ProjectCommandSource> projectLibraries;
        
        
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
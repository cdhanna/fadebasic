using System.Collections.Generic;
using System.Linq;

namespace FadeBasic.Virtual.HotReload
{
    /// <summary>
    /// The symbol-level facts about one compiled program that hot-reload diffs.
    /// Built cheaply from a <see cref="Compiler"/> (the "old side" is just the
    /// program's current compiler, which the <see cref="HotReloadSession"/>
    /// keeps; the "new side" is a fresh compile of the edited source). This is
    /// NOT a retained bytecode artifact — it is a light view over the compiler's
    /// symbol tables plus the raw program bytes.
    ///
    /// See <c>HOT_RELOAD_IMPLEMENTATION.md</c>.
    /// </summary>
    public sealed class ProgramFacts
    {
        /// <summary>global variable name -> symbol (register address, type).</summary>
        public Dictionary<string, CompiledVariable> Globals = new Dictionary<string, CompiledVariable>();

        /// <summary>global array name -> symbol.</summary>
        public Dictionary<string, CompiledArrayVariable> GlobalArrays = new Dictionary<string, CompiledArrayVariable>();

        /// <summary>struct type name -> layout (name-keyed fields with offsets).</summary>
        public Dictionary<string, CompiledType> TypesByName = new Dictionary<string, CompiledType>();

        /// <summary>instruction &lt;-&gt; source-statement map (may be null if compiled without debug data).</summary>
        public DebugData Debug;

        /// <summary>the bytecode.</summary>
        public byte[] Program;

        /// <summary>highest register slot + 1 (size the global register bank must be).</summary>
        public int MaxRegisterAddress;

        public static ProgramFacts FromCompiler(Compiler compiler)
        {
            var facts = new ProgramFacts
            {
                Debug = compiler.DebugData,
                Program = compiler.Program.ToArray(),
                MaxRegisterAddress = (int)compiler.globalScope.registerCount,
            };

            foreach (var kvp in compiler.globalScope.Variables)
            {
                facts.Globals[kvp.Key] = kvp.Value;
            }
            foreach (var kvp in compiler.globalScope.ArrayVariables)
            {
                facts.GlobalArrays[kvp.Key] = kvp.Value;
            }
            foreach (var type in compiler._typeTable.Values)
            {
                facts.TypesByName[type.typeName] = type;
            }

            return facts;
        }
    }
}

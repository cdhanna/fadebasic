using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using FadeBasic.Ast;
using FadeBasic.Json;

namespace FadeBasic.Virtual
{
    public class CompiledVariable : IJsonable, IJsonableSerializationCallbacks
    {
        public byte byteSize;
        public byte typeCode;
        public string name;
        public string structType;
        public ulong registerAddress;

        private string registerAddressSerializer;
        public bool isGlobal;
        
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(byteSize), ref byteSize);
            op.IncludeField(nameof(typeCode), ref typeCode);
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(structType), ref structType);
            op.IncludeField(nameof(registerAddressSerializer), ref registerAddressSerializer);
            op.IncludeField(nameof(isGlobal), ref isGlobal);
        }

        public void OnAfterDeserialized()
        {
            registerAddress = ulong.Parse(registerAddressSerializer);
        }

        public void OnBeforeSerialize()
        {
            registerAddressSerializer = registerAddress.ToString();
        }
    }

    public class CompiledArrayVariable : IJsonable, IJsonableSerializationCallbacks
    {
        public int byteSize;
        public byte typeCode;
        public string name;
        public CompiledType structType;
        public ulong registerAddress;

        private string registerAddressSerializer;
        
        public bool isGlobal;
        public byte[] rankSizeRegisterAddresses; // an array where the index is the rank, and the value is the ptr to a register whose value holds the size of the rank
        public byte[] rankIndexScalerRegisterAddresses; // an array where the index is the rank, and the value is the ptr to a register whose value holds the multiplier factor for the rank's indexing
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(byteSize), ref byteSize);
            op.IncludeField(nameof(typeCode), ref typeCode);
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(structType), ref structType);
            op.IncludeField(nameof(registerAddressSerializer), ref registerAddressSerializer);
            op.IncludeField(nameof(isGlobal), ref isGlobal);
            op.IncludeField(nameof(rankSizeRegisterAddresses), ref rankSizeRegisterAddresses);
            op.IncludeField(nameof(rankIndexScalerRegisterAddresses), ref rankIndexScalerRegisterAddresses);
        }

        public void OnAfterDeserialized()
        {
            registerAddress = ulong.Parse(registerAddressSerializer);
        }

        public void OnBeforeSerialize()
        {
            registerAddressSerializer = registerAddress.ToString();
        }
    }

    public class CompiledType : IJsonable
    {
        public string typeName;
        public int typeId;
        public int byteSize;
        public Dictionary<string, CompiledTypeMember> fields = new Dictionary<string, CompiledTypeMember>();
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(typeName), ref typeName);
            op.IncludeField(nameof(typeId), ref typeId);
            op.IncludeField(nameof(byteSize), ref byteSize);
            op.IncludeField(nameof(fields), ref fields);
        }
    }

    public struct CompiledTypeMember : IJsonable
    {
        public int Offset, Length;
        public byte TypeCode;
        public CompiledType Type;
        
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(Offset), ref Offset);
            op.IncludeField(nameof(Length), ref Length);
            op.IncludeField(nameof(TypeCode), ref TypeCode);
            op.IncludeField(nameof(Type), ref Type);
        }
    }

    public struct LabelReplacement
    {
        public int InstructionIndex;
        // Region-prefixed label key (see Compiler.MakeLabelKey). Built
        // at emit time from the current label region + the user-written
        // label name so two tests / two functions with same-named labels
        // resolve independently. Runto replacements use the main-body
        // region prefix regardless of where the `runto X` was written.
        public string Label;
    }

    public class TestManifestEntry : IJsonable
    {
        public string name;
        public int entryPointAddress;
        public bool isAbstract;
        public string fromParent; // null if no parent

        // sourceLine/sourceChar are reported in the ORIGINATING file's coordinate
        // space (1-based line numbers as the user sees them). The compiler
        // initially stamps these in the concatenated-source space; a post-compile
        // pass remaps them via SourceMap when one is available. The originating
        // file path goes in <see cref="sourceFilePath"/>; null/empty means the
        // file is unknown (no source map provided), and consumers should treat
        // line/char as best-effort positions only.
        public int sourceLine;
        public int sourceChar;
        public string sourceFilePath;

        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(entryPointAddress), ref entryPointAddress);
            op.IncludeField(nameof(isAbstract), ref isAbstract);
            op.IncludeField(nameof(fromParent), ref fromParent);
            op.IncludeField(nameof(sourceLine), ref sourceLine);
            op.IncludeField(nameof(sourceChar), ref sourceChar);
            op.IncludeField(nameof(sourceFilePath), ref sourceFilePath);
        }
    }

    /// <summary>
    /// Serializable wrapper around the compiler's test manifest. Used by
    /// <c>LaunchUtil.PackTestManifest</c> / <c>UnpackTestManifest</c> to bake
    /// the manifest into the generated launchable so console-app builds can
    /// support <c>--fade-test=name</c> at runtime.
    /// </summary>
    public class TestManifest : IJsonable
    {
        public List<TestManifestEntry> entries = new List<TestManifestEntry>();

        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(entries), ref entries);
        }
    }

    public struct FunctionCallReplacement
    {
        public int InstructionIndex;
        public string FunctionName;
    }

    public class CompileScope
    {
        public ulong registerCount;
        
        private Dictionary<string, CompiledVariable> _varToReg = new Dictionary<string, CompiledVariable>();

        private Dictionary<string, CompiledArrayVariable> _arrayVarToReg =
            new Dictionary<string, CompiledArrayVariable>();

        public CompileScope()
        {
            
        }

        public CompileScope(Dictionary<string, CompiledVariable> varToReg, Dictionary<string, CompiledArrayVariable> arrayVarToReg)
        {
            _varToReg = varToReg;
            _arrayVarToReg = arrayVarToReg;
            ulong highestAddress = 0;
            foreach (var kvp in _varToReg)
            {
                if (kvp.Value.registerAddress > highestAddress)
                {
                    highestAddress = kvp.Value.registerAddress;
                }
            }
            foreach (var kvp in _arrayVarToReg)
            {
                if (kvp.Value.registerAddress > highestAddress)
                {
                    highestAddress = kvp.Value.registerAddress;
                }

                foreach (var n in kvp.Value.rankSizeRegisterAddresses)
                {
                    if (n > highestAddress) highestAddress = n;
                }
                foreach (var n in kvp.Value.rankIndexScalerRegisterAddresses)
                {
                    if (n > highestAddress) highestAddress = n;
                }
            }

            registerCount = highestAddress + 1;
        }
        
        public bool TryGetVariable(string name, out CompiledVariable variable)
        {
            return _varToReg.TryGetValue(name, out variable);
        }

        public bool TryGetArray(string name, out CompiledArrayVariable arrayVariable)
        {
            return _arrayVarToReg.TryGetValue(name, out arrayVariable);
        }

        // Enumeration accessors used by hot-reload's structural diff to read the
        // full name -> register/type mapping out of a compiled program. Kept
        // read-only so callers can't mutate the scope's tables.
        public IReadOnlyDictionary<string, CompiledVariable> Variables => _varToReg;
        public IReadOnlyDictionary<string, CompiledArrayVariable> ArrayVariables => _arrayVarToReg;
        
        public CompiledVariable Create(string name, byte typeCode, bool isGlobal, byte regOffset=0)
        {
            var compileVar = new CompiledVariable
            {
                registerAddress = (regOffset + registerCount++),
                name = name,
                typeCode = typeCode,
                byteSize = TypeCodes.GetByteSize(typeCode),
                isGlobal = isGlobal
            };  

            _varToReg[name] = compileVar;
            
            return compileVar;
        }

        public CompiledArrayVariable CreateArray(string declarationVariable, int rankLength, byte typeCode, bool isGlobal)
        {
            var compileArrayVar = new CompiledArrayVariable()
            {
                registerAddress = (byte)(registerCount++),
                rankSizeRegisterAddresses = new byte[rankLength],
                rankIndexScalerRegisterAddresses = new byte[rankLength],
                name = declarationVariable,
                typeCode = typeCode,
                byteSize = TypeCodes.GetByteSize(typeCode),
                isGlobal = isGlobal
            };
            _arrayVarToReg[declarationVariable] = compileArrayVar;
            return compileArrayVar;
        }

        public byte AllocateRegister()
        {
            return (byte)(registerCount++);
        }
    }

    public class CompilerOptions
    {
        public bool GenerateDebugData = false;
        public bool InternStrings = true;

        public static readonly CompilerOptions Default = new CompilerOptions
        {
            GenerateDebugData = false,
            InternStrings = true
        };
    }

    public class InternedScopeMetadata : IJsonableSerializationCallbacks
    {
        public int scopeIndex;
        public ulong maxRegisterSize;
        private string maxRegisterSizeSerializer;
        
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(scopeIndex), ref scopeIndex);
            op.IncludeField(nameof(maxRegisterSizeSerializer), ref maxRegisterSizeSerializer);
        }

        public void OnAfterDeserialized()
        {
            maxRegisterSize = ulong.Parse(maxRegisterSizeSerializer);
        }

        public void OnBeforeSerialize()
        {
            maxRegisterSizeSerializer = maxRegisterSize.ToString();
        }
    }
    
    public class InternedData : IJsonable, IJsonableSerializationCallbacks
    {
        public Dictionary<string, InternedType> types;
        public Dictionary<string, InternedFunction> functions = new Dictionary<string, InternedFunction>();
        public List<InternedString> strings = new List<InternedString>();
        // public Dictionary<int, InternedScopeMetadata> scopeMetaDatas = new Dictionary<int, InternedScopeMetadata>();
        public ulong maxRegisterAddress;
        private string maxRegisterAddressSerializer;
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(types), ref types);
            op.IncludeField(nameof(functions), ref functions);
            op.IncludeField(nameof(strings), ref strings);
            op.IncludeField(nameof(maxRegisterAddressSerializer), ref maxRegisterAddressSerializer);
        }

        public void OnAfterDeserialized()
        {
            maxRegisterAddress = ulong.Parse(maxRegisterAddressSerializer);
        }

        public void OnBeforeSerialize()
        {
            maxRegisterAddressSerializer = maxRegisterAddress.ToString();
        }
    }
    
    public class InternedString : IJsonable
    {
        public string value;
        public int[] indexReferences;
        
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(value), ref value);
            op.IncludeField(nameof(indexReferences), ref indexReferences);
        }
    }

    public class InternedFunction : IJsonable
    {
        public string name;
        public int insIndex;
        
        public int typeCode;
        public int typeId;
        public List<InternedFunctionParameter> parameters = new List<InternedFunctionParameter>();
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(insIndex), ref insIndex);
            op.IncludeField(nameof(typeCode), ref typeCode);
            op.IncludeField(nameof(typeId), ref typeId);
     
            op.IncludeField(nameof(parameters), ref parameters);
        }
    }

    public class InternedFunctionParameter : IJsonable
    {
        public string name;
        public int index;
        public int typeCode;
        public int typeId;
        
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(index), ref index);
            op.IncludeField(nameof(typeCode), ref typeCode);
            op.IncludeField(nameof(typeId), ref typeId);
        }
    }

    public class InternedType : IJsonable
    {
        public string name;
        public int byteSize;
        public int typeId;
        public Dictionary<string, InternedField> fields = new Dictionary<string, InternedField>();
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(name), ref name);
            op.IncludeField(nameof(typeId), ref typeId);
            op.IncludeField(nameof(byteSize), ref byteSize);
            op.IncludeField(nameof(fields), ref fields);
        }
    }

    public class InternedField : IJsonable
    {
        public int offset, length;
        public byte typeCode;
        public string typeName;
        public int typeId;
        
        public void ProcessJson<T>(ref T op) where T : IJsonOperation
        {
            op.IncludeField(nameof(offset), ref offset);
            op.IncludeField(nameof(length), ref length);
            op.IncludeField(nameof(typeCode), ref typeCode);
            op.IncludeField(nameof(typeName), ref typeName);
            op.IncludeField(nameof(typeId), ref typeId);
        }
    }
    
    
    public class Compiler
    {
        // public readonly CommandCollection commands;
        private List<byte> _buffer = new List<byte>();
        private DebugData _dbg;
        public DebugData DebugData => _dbg;
        public List<byte> Program => _buffer;
        // public int registerCount;

        public CompileScope globalScope;
        public Stack<CompileScope> scopeStack;

        private CompileScope scope => scopeStack.Peek();
        
        private Dictionary<string, CompiledType> _types = new Dictionary<string, CompiledType>();
        public Dictionary<int, CompiledType> _typeTable = new Dictionary<int, CompiledType>();

        public HostMethodTable methodTable;

        private Dictionary<string, int> _commandToPtr = new Dictionary<string, int>();
        private Stack<List<int>> _exitInstructionIndexes = new Stack<List<int>>();
        private Stack<List<int>> _skipInstructionIndexes = new Stack<List<int>>();
        private readonly Stack<List<int>> _jumpIndexPool = new Stack<List<int>>();

        private List<int> RentJumpList()
        {
            if (_jumpIndexPool.Count > 0) { var l = _jumpIndexPool.Pop(); l.Clear(); return l; }
            return new List<int>();
        }

        private void ReturnJumpList(List<int> list) => _jumpIndexPool.Push(list);

        private List<LabelReplacement> _labelReplacements = new List<LabelReplacement>();
        // Keyed by region-prefixed label name. Region is empty for main
        // body, "test:<name>" inside a test body, "fn:<name>" inside a
        // function body. Each region has its own label namespace so two
        // tests / two functions can share label names without collision.
        private Dictionary<string, int> _labelToInstructionIndex = new Dictionary<string, int>();
        // The region currently being compiled. Compile(LabelDeclarationNode)
        // builds the key from this region; Compile(GotoStatement) /
        // Compile(GoSubStatement) stamp the region-prefixed key into the
        // emitted replacement so resolution stays scoped.
        private string _currentLabelRegion = "";

        // Compose a label dictionary key from a region + user-written
        // label name. The `::` separator can't appear in either piece
        // (region names are compiler-generated, label names are restricted
        // to identifier characters) so the encoding is unambiguous.
        private static string MakeLabelKey(string region, string label)
            => (region ?? "") + "::" + label;

        // For each `runto label` call site, record where in the bytecode the
        // PUSH int placeholder lives so we can patch the resolved post-yield
        // address (label_addr + 2) at the end of compilation.
        private List<LabelReplacement> _runtoReplacements = new List<LabelReplacement>();

        // Set of label names that any test references via `runto`. These get a
        // RUNTO_YIELD opcode emitted right after the label's NOOP. Labels that
        // aren't runto targets carry no overhead.
        private HashSet<string> _runtoTargetLabels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // Manifest of compiled tests: name → entry-point address. Recorded as
        // each test body is compiled. Surfaced via the public Manifest property
        // and (later) emitted into the interned-data section.
        private List<TestManifestEntry> _testManifest = new List<TestManifestEntry>();
        public IReadOnlyList<TestManifestEntry> TestManifest => _testManifest;

        private List<FunctionCallReplacement> _functionCallReplacements = new List<FunctionCallReplacement>();
        private Dictionary<string, int> _functionTable = new Dictionary<string, int>();

        // For each `assert` failure-branch emit site, record the buffer index of
        // the placeholder PUSH int that should be patched with the assert-unwind
        // trampoline's address. The trampoline is emitted once near program end;
        // these get patched after that emission completes.
        private List<int> _assertTrampolinePatches = new List<int>();
        
        private InternedData data = new InternedData();
        
        public Dictionary<string, HashSet<int>> stringToCallingInstructionIndexes =
            new Dictionary<string, HashSet<int>>();

        private CompilerOptions _options;


        public Compiler(CommandCollection commands, CompilerOptions options=null, CompileScope givenGlobalScope=null)
        {
            _options = options;
            _options ??= CompilerOptions.Default;
            if (_options.GenerateDebugData)
            {
                _dbg = new DebugData();
            }

            var methods = new CommandInfo[commands.Commands.Count];
            for (var i = 0; i < commands.Commands.Count; i++)
            {
                methods[i] = commands.Commands[i];
                _commandToPtr[commands.Commands[i].UniqueName] = i;
            }
            methodTable = new HostMethodTable
            {
                methods = methods
            };

            scopeStack = new Stack<CompileScope>();
            globalScope = givenGlobalScope ?? new CompileScope();
            scopeStack.Push(globalScope);
        }

        public void AddType(CompiledType type)
        {
            _types.Add(type.typeName, type);
            _typeTable.Add(type.typeId, type);
        }

        public void AddFunction(string functionName, int insIndex)
        {
            _functionTable.Add(functionName, insIndex);
        }
        

        public void Compile(ProgramNode program)
        {

            // push a temporary value that will be replaced later.
            //  this value represents the ins-ptr where the interned-data lives.
            AppendInt32(_buffer, 0);

            // Pre-pass: collect every label name referenced by a `runto` anywhere in
            // the test corpus. These labels get a RUNTO_YIELD opcode emitted right
            // after their NOOP. Labels not referenced get no overhead, and run builds
            // with no tests skip this pass entirely (set stays empty).
            CollectRuntoTargets(program);

            foreach (var typeDef in program.typeDefinitions)
            {
                Compile(typeDef);
            }

            foreach (var statement in program.statements)
            {

                Compile(statement);
            }

            // prevent the execution from ever going to the functions. GOTO statements _should_ be illegal to jump into a function's scope.
            CompileEnd();

            foreach (var function in program.functions)
            {
                Compile(function);
            }

            // Tests are compiled as additional, runnable bytecode regions after the
            // program's functions but before interned data. Each test gets its own
            // entry point recorded in the manifest. A test instance is launched via
            // `new VirtualMachine(program, manifest.entryPointAddress)`.
            //
            // Two-phase emission so `from`-chains work without duplicating body
            // bytecode: phase 1 lays down each test's body region (statements +
            // RETURN, then any test-scoped functions) and records its start
            // address; phase 2 lays down each test's launcher region (a flat
            // sequence of JUMP_HISTORY → ancestor-body, ..., JUMP_HISTORY → self-
            // body, HALT) and stamps the manifest with the launcher address.
            // Running a test = jump to its launcher, which GOSUBs through the
            // full chain in order, sharing the VM's scope/registers/mock-table.
            var testBodyAddresses = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
            foreach (var test in program.tests)
            {
                CompileTestBody(test, testBodyAddresses);
            }
            foreach (var test in program.tests)
            {
                CompileTestLauncher(test, testBodyAddresses, program.tests);
            }

            // Emit the assert-unwind trampoline after tests, before interned
            // data. ASSERT_FAIL sites pushed a placeholder for its address; the
            // call below patches every site once the real address is known.
            CompileAssertUnwindTrampoline();

            { // handle interned data
                { // replace the jump ptr at index=0 to tell us where the data lives.
                    PatchInt32(_buffer, 0, _buffer.Count);
                }

                PushInternedData();
            }
            CompileJumpReplacements();
        }

        private void CollectRuntoTargets(ProgramNode program)
        {
            void Visit(IAstVisitable node)
            {
                if (node is RuntoStatement runto)
                {
                    _runtoTargetLabels.Add(runto.targetLabel);
                }
            }
            foreach (var test in program.tests)
            {
                test.Visit(Visit);
            }
        }

        // Phase 1: emit a test's body region (statements + RETURN), then any
        // test-scoped functions. Records the body's start address in
        // `bodyAddresses` so phase 2 launchers can GOSUB to it. Manifest
        // entry is added in phase 2 (so it points at the launcher, not the
        // body) — but we tag the body's start instruction for debugger
        // function-name resolution here.
        private void CompileTestBody(TestNode test,
            Dictionary<string, int> bodyAddresses)
        {
            var bodyStart = _buffer.Count;
            if (test.name != null)
            {
                bodyAddresses[test.name] = bodyStart;
            }

            // Set the label region for this test so two tests with same-
            // named labels don't collide at jump-replacement time. Restore
            // on the way out — nested compile of test-scoped functions
            // below will set their own regions over this.
            var prevRegion = _currentLabelRegion;
            _currentLabelRegion = "test:" + (test.name ?? "<anon>");

            // Compile the test body. The dispatch in Compile(IStatementNode) skips
            // FunctionStatement nodes — they're emitted separately below — so the
            // body's own function declarations don't pollute the test's entry-point
            // bytecode region.
            foreach (var statement in test.testProgram.statements)
            {
                Compile(statement);
            }

            // RETURN instead of HALT: when invoked via the launcher's
            // JUMP_HISTORY, this returns control so the next ancestor-or-
            // self body in the chain can run.
            //
            // DEFER drains here are intentionally OMITTED. A `from`-child
            // is semantically a continuation of its parent — parent's
            // teardown (defer) statements should fire at the END of the
            // chain, after the child's body, not at parent's RETURN.
            // Defers register on the shared deferredJumps stack; the
            // launcher's CompileEnd() drains them once after every body
            // in the chain has run. Standalone tests get identical
            // behavior — the launcher's drain runs after a single body.
            _buffer.Add(OpCodes.RETURN);

            // Restore the label region. Test-scoped functions emitted below
            // re-set their own region inside Compile(FunctionStatement).
            _currentLabelRegion = prevRegion;

            // Now compile any test-scoped functions. They live alongside program
            // functions in the bytecode blob and register themselves in the
            // shared _functionTable, which means the test body can call them by
            // name. (Stage 6 narrows visibility via the from-chain in a follow-up
            // pass; for v1 they're globally addressable, which is permissive.)
            foreach (var function in test.testProgram.functions)
            {
                Compile(function);
            }
        }

        // Phase 2: emit a test's launcher region — what the manifest's
        // entryPointAddress points to. Walks the from-chain (root → self,
        // skipping any test whose body we don't have, which covers
        // chain-broken cases the visitor already errored on) and emits a
        // JUMP_HISTORY → body for each, finishing with a HALT.
        //
        // Each ancestor's body ends with RETURN, popping the launcher's
        // pushed return frame so the next JUMP_HISTORY fires. State
        // (registers, mock table, runto position) flows naturally between
        // segments because they share the same VM context.
        private void CompileTestLauncher(TestNode test,
            Dictionary<string, int> bodyAddresses,
            List<TestNode> allTests)
        {
            var launcherStart = _buffer.Count;
            _testManifest.Add(new TestManifestEntry
            {
                name = test.name,
                entryPointAddress = launcherStart,
                isAbstract = test.isAbstract,
                fromParent = test.fromParent,
                sourceLine = test.startToken?.lineNumber ?? 0,
                sourceChar = test.startToken?.charNumber ?? 0
            });

            var chain = ResolveTestFromChain(test, allTests);
            foreach (var member in chain)
            {
                if (!bodyAddresses.TryGetValue(member.name ?? "", out var bodyAddr))
                {
                    // No body recorded — likely a cycle-broken test the
                    // visitor already flagged. Skip it to avoid an unresolved
                    // GOSUB target.
                    continue;
                }
                AddPushInt(_buffer, bodyAddr);
                _buffer.Add(OpCodes.JUMP_HISTORY_LAUNCH);
            }
            CompileEnd();
        }

        // Walk a test's from-chain from root to self. Bail with just
        // [self] if we detect a cycle so we don't loop forever; the
        // visitor's TestFromParentCycle error tells the user what's wrong.
        // Unknown parents are similarly cut off — the chain stops where
        // the name fails to resolve.
        private List<TestNode> ResolveTestFromChain(TestNode test,
            List<TestNode> allTests)
        {
            var byName = new Dictionary<string, TestNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var t in allTests)
            {
                if (t.name != null) byName[t.name] = t;
            }

            var chain = new List<TestNode> { test };
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (test.name != null) visited.Add(test.name);

            var cursor = test;
            while (cursor.fromParent != null
                && byName.TryGetValue(cursor.fromParent, out var parent))
            {
                if (parent.name != null && !visited.Add(parent.name))
                {
                    // Cycle — bail. The chain we have so far isn't useful
                    // (we'd loop), so prefer "self only" semantics.
                    return new List<TestNode> { test };
                }
                chain.Add(parent);
                cursor = parent;
            }
            chain.Reverse(); // root first
            return chain;
        }

        public void CompileJumpReplacements()
        {

            // replace all label instructions...
            foreach (var replacement in _labelReplacements)
            {
                if (!_labelToInstructionIndex.TryGetValue(replacement.Label, out var location))
                {
                    throw new Exception("Compiler: unknown label location " + replacement.Label);
                }

                PatchInt32(_buffer, replacement.InstructionIndex + 2, location);
            }

            // replace all runto target placeholders. The runto target address is
            // the byte AFTER the label's RUNTO_YIELD opcode (= label_addr + 2).
            // RUNTO_YIELD checks `runtoStack.Peek().target == instructionIndex`,
            // and instructionIndex at that point is post-RUNTO_YIELD, so we need
            // to bake `label_addr + 2` into the PUSH int placeholder. Runto
            // always targets MAIN-BODY labels regardless of where `runto X`
            // was written, so the lookup uses the main-body region prefix.
            foreach (var replacement in _runtoReplacements)
            {
                var mainKey = MakeLabelKey("", replacement.Label);
                if (!_labelToInstructionIndex.TryGetValue(mainKey, out var location))
                {
                    throw new Exception("Compiler: unknown runto target label " + replacement.Label);
                }

                var postYieldAddr = location + 2; // skip the NOOP and the RUNTO_YIELD
                PatchInt32(_buffer, replacement.InstructionIndex + 2, postYieldAddr);
            }

            // replace all function instrunctions
            foreach (var replacement in _functionCallReplacements)
            {
                if (!_functionTable.TryGetValue(replacement.FunctionName, out var location))
                {
                    throw new Exception("Compiler: unknown function location " + replacement.FunctionName);
                }

                PatchInt32(_buffer, replacement.InstructionIndex + 2, location);
            }
        }

        public void PushInternedData()
        {

            { // handle the strings
                data.strings = new List<InternedString>();
                foreach (var kvp in stringToCallingInstructionIndexes)
                {
                    var internedString = new InternedString
                    {
                        value = kvp.Key,
                        indexReferences = kvp.Value.ToArray()
                    };
                    data.strings.Add(internedString);
                }
            }
            
            // the type table will be the JSONified
            data.types = new Dictionary<string, InternedType>();
            foreach (var kvp in _types)
            {
                var type = new InternedType
                {
                    typeId = kvp.Value.typeId,
                    name = kvp.Key,
                    byteSize = kvp.Value.byteSize,
                };

                foreach (var fieldKvp in kvp.Value.fields)
                {
                    var field = new InternedField
                    {
                        length = fieldKvp.Value.Length,
                        offset = fieldKvp.Value.Offset,
                        typeCode = fieldKvp.Value.TypeCode,
                        typeName = fieldKvp.Value.Type?.typeName,
                        typeId = fieldKvp.Value.Type?.typeId ?? 0
                    };
                    type.fields.Add(fieldKvp.Key, field);
                }
                
                data.types.Add(type.name, type);
            }

            data.maxRegisterAddress = scopeStack.Peek().registerCount;
            data.maxRegisterAddress += 1; // an extra 1 for debugging room.
            var json = data.Jsonify();
            var jsonBytes = Encoding.Default.GetBytes(json);
            _buffer.AddRange(jsonBytes);
        }

        public void Compile(TypeDefinitionStatement typeDefinition)
        {
            /*
             * compile all the type definitions first!
             * for each type, we need to pre-compute the offset _per_ field
             * and we need to calculate the total size for the struct
             */
            var typeName = typeDefinition.name.variableName;
            var type = new CompiledType
            {
                typeId = _typeTable.Count + 1, // include the +1 to imply that a typeId of 0 is invalid (if you see 0, its implies a bug happened)
                typeName = typeName
            };
            
            int totalSize = 0;
            foreach (var decl in typeDefinition.declarations)
            {
                var fieldOffset = totalSize;
                var typeMember = new CompiledTypeMember
                {
                    Offset = fieldOffset,
                };

                int size = 0;
                switch (decl.type)
                {
                    case TypeReferenceNode typeRef:
                        var tc = VmUtil.GetTypeCode(typeRef.variableType);
                        size = TypeCodes.GetByteSize(tc);
                        typeMember.Length = size;
                        totalSize += size;
                        typeMember.TypeCode = tc;
                        break;
                    case StructTypeReferenceNode structTypeRef:
                        if (!_types.TryGetValue(structTypeRef.variableNode.variableName, out var structType))
                        {
                            throw new Exception("Referencing type that does not exist yet. " + structTypeRef.variableNode);
                        }

                        size = structType.byteSize;
                        typeMember.Length = size;
                        totalSize += size;
                        typeMember.TypeCode = TypeCodes.STRUCT;
                        typeMember.Type = structType;
                        break;
                }
                
                type.fields.Add(decl.name.variableName, typeMember);
            }

            type.byteSize = totalSize;
            
            _types[typeName] = type;
            _typeTable[type.typeId] = type;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        void AddDebugToken(Token token, int insIndex=-1)
        {
            if (_dbg == null) return; // no-op if we are not generating debugger info.
            if (insIndex < 0) 
                insIndex = _buffer.Count;
            _dbg.AddStatementDebugToken(insIndex, token);
        }

        
        // [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // void AddStartDebugToken(Token token)
        // {
        //     if (_dbg == null) return; // no-op if we are not generating debugger info.
        //     _dbg.AddStartToken(_buffer.Count, token); // this happens BEFORE the byte code is emitted. 
        // }
        // [MethodImpl(MethodImplOptions.AggressiveInlining)]
        // void AddStopDebugToken(Token token)
        // {
        //     if (_dbg == null) return; // no-op if we are not generating debugger info.
        //     _dbg.AddStopToken(_buffer.Count - 1, token); // this happens AFTER the byte code is emitted. 
        // }

        public void Compile(IStatementNode statement)
        {
            /*
             * every statement can have a breakpoint.
             *  You can step OVER a statement, which we should capture at the end of this function.
             *  That isn't quite right, because if you step over an IF statement, you don't jump over the entire branch...
             */
            switch (statement)
            {
                case CommentStatement _:
                    break;
                default:
                    AddDebugToken(statement.StartToken);
                    break;
            }
            
            
            switch (statement)
            {
                case CommentStatement _:
                    // ignore comments
                    break;
                case DeclarationStatement declarationStatement:
                    Compile(declarationStatement);
                    break;
                case RedimStatement redimStatement:
                    Compile(redimStatement);
                    break;
                case AssignmentStatement assignmentStatement:
                    Compile(assignmentStatement);
                    break;
                case CommandStatement commandStatement:
                    Compile(commandStatement);
                    break;
                case LabelDeclarationNode labelStatement:
                    Compile(labelStatement);
                    break;
                case GotoStatement gotoStatement:
                    Compile(gotoStatement);
                    break;
                case GoSubStatement goSubStatement:
                    Compile(goSubStatement);
                    break;
                case ReturnStatement returnStatement:
                    Compile(returnStatement);
                    break;
                case EndProgramStatement endProgramStatement:
                    Compile(endProgramStatement);
                    break;
                case IfStatement ifStatement:
                    Compile(ifStatement);
                    break;
                case ExitLoopStatement exitStatement:
                    Compile(exitStatement);
                    break;
                case SkipLoopStatement skipStatement:
                    Compile(skipStatement);
                    break;
                case WhileStatement whileStatement:
                    Compile(whileStatement);
                    break;
                case RepeatUntilStatement repeatUntilStatement:
                    Compile(repeatUntilStatement);
                    break;
                case ForStatement forStatement:
                    Compile(forStatement);
                    break;
                case DoLoopStatement doLoopStatement:
                    Compile(doLoopStatement);
                    break;
                case SwitchStatement switchStatement:
                    Compile(switchStatement);
                    break;
                case FunctionStatement _:
                    // functions should be compiled at the end... ignoring for now.
                    break;
                case FunctionReturnStatement returnStatement:
                    Compile(returnStatement);
                    break;
                case RuntoStatement runtoStatement:
                    Compile(runtoStatement);
                    break;
                case AssertStatement assertStatement:
                    Compile(assertStatement);
                    break;
                case MockStatement mockStatement:
                    Compile(mockStatement);
                    break;
                case MockExitMockStatement mockReturnsStatement:
                    Compile(mockReturnsStatement);
                    break;
                case MockForbidStatement mockForbidStatement:
                    Compile(mockForbidStatement);
                    break;
                case ClearMockStatement clearMockStatement:
                    Compile(clearMockStatement);
                    break;
                case ExpressionStatement expressionStatement:
                    Compile(expressionStatement);
                    break;
                case MacroTokenizeStatement tokenizeStatement: 
                    Compile(tokenizeStatement);
                    break;
                case DeferStatement deferStatement:
                    Compile(deferStatement);
                    break;
                    
                default:
                    throw new Exception("compiler exception: unhandled statement node " + statement);
            }
        }

        /// <summary>
        /// this stack represents nested scope PUSH/POPS
        /// </summary>
        private Stack<DeferGroup> deferredStatementStack = new Stack<DeferGroup>();

        public class DeferGroup
        {
            // public int pushMetaDataIndex;
            // public List<int> deferJumpIndexes;
            public int count;
        }
        
        
        void HandleDeferExit()
        {
            
            // keep track of the start of the loop by remembering this address
            var loopStartAddress = _buffer.Count;
            
            // leave the return address on the stack to come back to.
            AddPushInt(_buffer, loopStartAddress);
            
            // pull the defer site
            _buffer.Add(OpCodes.POP_DEFER);
            
            // duplicate that address, so that we can use it in the jump_zero
            _buffer.Add(OpCodes.DUPE);
            
            // push the site of the end of the loop (fill this in later)
            var replaceExitIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            
            // if zero, then jump to end of this loop
            _buffer.Add(OpCodes.JUMP_ZERO);
            
            // if we did not jump, then we can jump to the start of the loop
            _buffer.Add(OpCodes.JUMP);
            
            // NOTE: the defer statement itself is going to jump back to the start of the loop.
            
            // this is the end
            var loopEndAddress = _buffer.Count;
            
            // discard the lagging 0 from the duped defer stack.
            _buffer.Add(OpCodes.DISCARD_TYPED);
            _buffer.Add(OpCodes.DISCARD_TYPED);
            
            // fix the end value
            PatchInt32(_buffer, replaceExitIndex + 2, loopEndAddress);
        }
        void CompilePopScope()
        {
            HandleDeferExit();
            _buffer.Add(OpCodes.POP_SCOPE);
        }

        void CompilePushScope()
        {
            _buffer.Add(OpCodes.PUSH_SCOPE);
        }
        
        void Compile(DeferStatement deferStatement)
        {
            // add some data that says we will jump to this location
            var deferAddrIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.PUSH_DEFER);
            
            // Jump over the actual deferred statement!
            // push a temporary address that we will replace with the ending of this statement
            var exitAddrIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
            
            var deferAddrValue = _buffer.Count;
            { // we are executing the defer at this point. 
                // then compile all of the statements inside this label
                foreach (var statement in deferStatement.statements)
                {
                    Compile(statement);
                }

                // return to the caller, so we can move to the next defer if it exists
                //  note: this is expecting to pull a value from the stack that was pushed
                //        before this defer statement started executing. (HandleDeferExit)
                _buffer.Add(OpCodes.JUMP);
            }
            
            // this location is the place the defer should jump execution to. 
            var exitAddr = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);
            PatchInt32(_buffer, exitAddrIndex + 2, exitAddr);
            // fix up the defer add
            PatchInt32(_buffer, deferAddrIndex + 2, deferAddrValue);
        }

        void Compile(MacroTokenizeStatement tokenizeStatement)
        {
            // need to compile a set of substs, stick their stringified results back into the token stream. 
            // TODO: only valid to do this when running with "higher context".
            
            // need to emit a new op-code that instructs the higher-context that tokenization is happening from X to Y, and this given moment.
            //  because variable state can change between tokenization blocks, so the order is very important. 

            var s = tokenizeStatement.startToken;
            var e = tokenizeStatement.endToken;

            // var tokens = tokenizeStatement.tokens;
            
            // build up a To-String()-ified version of all the tokens. 
            var raw = new StringBuilder();
            var substIndex = 0;
            // var substIndexMap = new Dictionary<int, int>(); // convert the index of the substitution to the index in the final raw string.
            // for (var i = 0; i <= tokens.Count; i++)
            // {
            //     if (tokenizeStatement.substitutions.Count > substIndex)
            //     {
            //         var curr = tokenizeStatement.substitutions[substIndex];
            //         if (curr.substitutionIndex == i)
            //         {
            //             substIndexMap[substIndex] = raw.Length;
            //             substIndex++;
            //         }
            //     }
            //
            //     if (i < tokens.Count)
            //     {
            //         raw.Append(tokens[i].raw); // ignore white-space, who cares?
            //         raw.Append(" "); // force whitespace. Something is wrong here. 
            //     }
            // }

            // var text = raw.ToString();
            
            // for each substitution...
            for (var i = 0; i < tokenizeStatement.substitutions.Count; i++)
            {
                // also compile the expression value itself
                Compile(tokenizeStatement.substitutions[i].innerExpression);
                
                // push the string index where the result should be inserted
                // AddPushInt(_buffer, substIndexMap[i]); // TODO: can this fail?
                AddPushInt(_buffer, tokenizeStatement.substitutions[i].substitutionIndex);
                AddPushInt(_buffer, tokenizeStatement.substitutions[i].tokenStartIndex);
                AddPushInt(_buffer, tokenizeStatement.substitutions[i].tokenEndIndex);
                
                // push a number saying if the expression should be stringified
                AddPushInt(_buffer, (tokenizeStatement.substitutions[i].isStringify 
                    ? 1 
                    : 0));
                
                // push a number saying if the expression was using a haunted variable or not. 
                AddPushInt(_buffer, (tokenizeStatement.substitutions[i].innerExpression.TransitiveFlags.HasFlag(TransitiveTypeFlags.Haunted) 
                    ? 1 
                    : 0));
            }
            
            // by compiling in the string, it will get interned
            // Compile(new LiteralStringExpression(tokenizeStatement.startToken, text));
            AddPushInt(_buffer, tokenizeStatement.startTokenIndex);
            AddPushInt(_buffer, tokenizeStatement.endTokenIndex);
            AddPushInt(_buffer, tokenizeStatement.tokenBlockIndex);
            
            // push the number of substitutions in this block. 
            AddPushInt(_buffer, tokenizeStatement.substitutions.Count);
            
            // then push the op-code that tells the VM to special-case the macro
            _buffer.Add(OpCodes.TOKENIZE);
            
            // compile the substitions
            var replacementIndexes = new List<int>();
            for (var i = 0; i < tokenizeStatement.substitutions.Count; i++)
            {
                var subst = tokenizeStatement.substitutions[i];
                
                // if execution ever reaches this subst, it is invalid, because it is just a value without any meaning. 
                // so we would need to jump over it. 
                // and then jump to the exit block
                replacementIndexes.Add(_buffer.Count);
                AddPushInt(_buffer, int.MaxValue);
                _buffer.Add(OpCodes.JUMP);
                
                // compile the value of the expression onto the program stack. 
                // Compile(subst.innerExpression);
                
                // TODO: the higher context needs to know how to get the values out of these compilations, and put them in the appropriate slots. 
                // and log a 
                
                // TODO: in general, the higher context should know to stop executing by this point. Maybe it would be better to use a JUMP to end of program? 
                _buffer.Add(OpCodes.EXPLODE);
            }

            // this is the end of the tokenization substitutions block, so execution can safely jump here. 
            var exitAddr = _buffer.Count;
            foreach (var exitIns in replacementIndexes)
                PatchInt32(_buffer, exitIns + 2, exitAddr);
        }

        void CompileAsInvocation(ArrayIndexReference expr)
        {
            // need to push values onto stack
            foreach (var argExpr in expr.rankExpressions)
            {
                Compile(argExpr);
            }
            
            _functionCallReplacements.Add(new FunctionCallReplacement()
            {
                InstructionIndex = _buffer.Count,
                FunctionName = expr.variableName
            });
            AddPushInt(_buffer, int.MaxValue); // temp ptr value, will be replaced by function location later.
            _buffer.Add(OpCodes.JUMP_HISTORY);
        }

        
        private void Compile(FunctionReturnStatement returnStatement)
        {
            // put the return value onto the stack
            if (returnStatement.returnExpression != null)
            {
                Compile(returnStatement.returnExpression);
            }

            // pop a scope
            CompilePopScope();
            
            // and then jump home
            _buffer.Add(OpCodes.RETURN);
        }

        private void Compile(FunctionStatement functionStatement)
        {
            // well, first, if we come across one of these, we should throw an exception...
            _buffer.Add(OpCodes.EXPLODE); // TODO: add a jump-over-function feature
            
            // functions are global
            var ptr = _buffer.Count;
            _functionTable[functionStatement.name] = ptr; // TODO: what about duplicate function names?

            var internedFunction = new InternedFunction
            {
                name = functionStatement.name,
                insIndex = ptr,
            };
            if (functionStatement.hasNoReturnExpression || functionStatement.ParsedType.type == VariableType.Void)
            {
                internedFunction.typeId = -1;
            }
            else
            {
                var tc = VmUtil.GetTypeCode(functionStatement.ParsedType.type);
                internedFunction.typeCode = tc;
                if (tc == TypeCodes.STRUCT)
                {
                    internedFunction.typeId = _types[functionStatement.ParsedType.structName].typeId;
                }
            }
            
            data.functions.Add(functionStatement.name, internedFunction);
            
            // at the insIndex, take note of the name for the debug data. Later, the index that has the 
            _dbg?.AddFunction(ptr, functionStatement.nameToken);

            // push a new scope
            CompilePushScope();

            // Labels inside this function get their own region — two
            // functions can share label names without resolving to each
            // other's body. Restored at function end.
            var prevRegion = _currentLabelRegion;
            _currentLabelRegion = "fn:" + functionStatement.name;

            // now, we need to pull values off the stack and put them into variable declarations...
            // foreach (var arg in functionStatement.parameters)
            for (var i = functionStatement.parameters.Count - 1; i >= 0; i --) // read in reverse order due to stack
            {
                var arg = functionStatement.parameters[i];
                
                // compile up a fake declaration for the input
                var fakeDecl = new DeclarationStatement
                {
                    variableNode = arg.variable,
                    scopeType = DeclarationScopeType.Local,
                    type = arg.type
                };
                var parameterTc = VmUtil.GetTypeCode(arg.type.variableType);
                
                var internedParameter = new InternedFunctionParameter
                {
                    name = fakeDecl.variable,
                    index = i,
                    typeCode = parameterTc
                };
                if (fakeDecl.type is StructTypeReferenceNode structType)
                {
                    internedParameter.typeId = _types[structType.variableNode.variableName].typeId;
                }
                
                internedFunction.parameters.Add(internedParameter);
                Compile(fakeDecl);
                
                // and now compile up the assignment
                _buffer.Add(OpCodes.CAST);

                var tc = VmUtil.GetTypeCode(arg.type.variableType);
                _buffer.Add(tc);
                CompileAssignmentLeftHandSide(arg.variable);

            }
            
           
            // compile all the statements...
            foreach (var statement in functionStatement.statements)
            {
                Compile(statement);
            }
            
            // at the end of the function, we need to jump home
            // pop a scope
            CompilePopScope();

            // and then jump home
            _buffer.Add(OpCodes.RETURN);

            _currentLabelRegion = prevRegion;
        }

        private void Compile(ExitLoopStatement exitLoopStatement)
        {
            // immediately jump to the exit...
            _exitInstructionIndexes.Peek().Add(_buffer.Count);

            // and then jump to the exit block
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
        }
        
        private void Compile(SkipLoopStatement skipLoopStatement)
        {
            // immediately jump to the start of the loop...
            _skipInstructionIndexes.Peek().Add(_buffer.Count);

            // and then jump to the beginning of the loop (this will be replaced later)
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
        }

        private void Compile(SwitchStatement switchStatement)
        {
            // first, compile the switch expression
            Compile(switchStatement.expression);

            // then, push the address of the default case (fill it in later)
            var defaultInsIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            
            // then, push the switch-value and address for each case
            var pairInsIndexes = new List<int>[switchStatement.cases.Count];
            var caseCount = 0;
            for (var i = 0; i < switchStatement.cases.Count; i++)
            {
                var caseStatement = switchStatement.cases[i];

                pairInsIndexes[i] = new List<int>();
                foreach (var literal in caseStatement.values)
                {
                    caseCount++; // keep track of how many ACTUAL cases there are
                    pairInsIndexes[i].Add(_buffer.Count); // later, we'll update the pushed int to be the address of the case
                    AddPushInt(_buffer, int.MaxValue);
                    
                    // compile the switch-value (this must be a literal, enforced by the parser)
                    Compile(literal);
                }
            }
            
            // push the total number of cases
            AddPushInt(_buffer, caseCount);
            
            // then, actually put the jump table.... It will jump to the right case, or default!
            _buffer.Add(OpCodes.JUMP_TABLE);
            
            // keep track of the actual address values for each case statement
            var caseAddrValues = new int[switchStatement.cases.Count];
            
            // keep track of the instruction indexes that point to exit-address, that need to be patched later
            var exitInsIndexes = new int[switchStatement.cases.Count];

            // compile each case block
            for (var i = 0; i < switchStatement.cases.Count; i++)
            {
                // the start of this case statement.
                var caseStatement = switchStatement.cases[i];
                caseAddrValues[i] = _buffer.Count; 
                
                // compile the actual statements...
                foreach (var statement in caseStatement.statements)
                {
                    Compile(statement);
                }
                
                // now that we are done with the case, jump to the end. (no "fall-through")
                exitInsIndexes[i] = _buffer.Count;
                AddPushInt(_buffer, int.MaxValue); // later, this will get changed to be the exit address
                _buffer.Add(OpCodes.JUMP);
            }
            
            // compile the default case
            var defaultAddr = _buffer.Count; // this is where the default case lives
            if (switchStatement.defaultCase != null)
            {
                foreach (var statement in switchStatement.defaultCase.statements)
                {
                    Compile(statement);
                }
            }

            // now at the end of the default block, we are done! so this is the exit address.
            var exitAddr = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);

            // Stamp a *computed* debug token on the exit NOOP. Every case body
            // exits by jumping here, and without a token of its own the exit
            // resolves (via TryFindClosestTokenBeforeIndex) to the LAST case
            // body's real token — so stepping over a matched case would wrongly
            // pause on the final CASE line even though it never executed. A
            // computed token is skipped by the stepper (mirrors Compile(IfStatement)).
            _dbg?.AddFakeDebugToken(exitAddr, switchStatement.endToken);

            // now do all the address replacements....
            for (var i = 0; i < switchStatement.cases.Count; i++)
            {
                var caseAddr = caseAddrValues[i];
                foreach (var index in pairInsIndexes[i])
                    PatchInt32(_buffer, index + 2, caseAddr);
            }

            PatchInt32(_buffer, defaultInsIndex + 2, defaultAddr);

            foreach (var exitIns in exitInsIndexes)
                PatchInt32(_buffer, exitIns + 2, exitAddr);
        }
        
        private void Compile(ForStatement forStatement)
        {
            // for later, we'll need a statement that adds the step expr
            var stepAssignment = new AssignmentStatement
            {
                expression = new BinaryOperandExpression
                {
                    operationType = OperationType.Add,
                    lhs = forStatement.variableNode,
                    rhs = forStatement.stepValueExpression
                },
                variable = forStatement.variableNode
            };
            
            // first, set the iterator variable to the start value
            var fakeAssignment = new AssignmentStatement
            {
                expression = forStatement.startValueExpression,
                variable = forStatement.variableNode
            };
            Compile(fakeAssignment);

            // then, keep track of the start of the for-loop, this is where we'll come back to
            var forLoopValue = _buffer.Count;
            
            // push min
            Compile(forStatement.startValueExpression); // TODO: we are accessing the start twice- that may mean a function gets called twice
            
            // push max
            Compile(forStatement.endValueExpression);

            // we don't actually know if the min is min, and the max is max; so use an op code to sort the previous two stack entries
            _buffer.Add(OpCodes.MIN_MAX_PUSH);
            
            // push x again
            Compile(forStatement.variableNode);
            
            // is x less than max?
            _buffer.Add(OpCodes.LTE);
            
            // if this is a zero, then we have failed, and we can exit...
            // push the address we want to go if failed
            var lteExitJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            // and maybe jump
            _buffer.Add(OpCodes.JUMP_ZERO);
            
            // push x again
            Compile(forStatement.variableNode);
            
            // is x greater than min?
            _buffer.Add(OpCodes.GTE);
            
            // then, put a fake value in for the for-statement success jump... We'll fix it later.
            var successJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            
            // then, do the jump-gt-zero
            _buffer.Add(OpCodes.JUMP_GT_ZERO);
            
            // if we didn't jump, then we need to load exit the for-loop.
            // Just take note of this buffer index, and we'll update it later
            var exitJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
            
            // keep track of the first index of the success
            var successJumpValue = _buffer.Count;
            _exitInstructionIndexes.Push(RentJumpList());
            _skipInstructionIndexes.Push(RentJumpList());
            foreach (var successStatement in forStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();
            var skipStatementIndexes = _skipInstructionIndexes.Pop();

            // This is the location where Step updates and evaluation happens
            // (important as skip should jump here, not to the very start)
            var stepLoopValue = _buffer.Count;

            // now to update the value of x, we need to add the stepExpr to it.
            Compile(stepAssignment); // NOTE: there could be a bug here, because we are looping on a deterministic math operation, but simulating the interpolated variable

            // jump back to the start
            AddPushInt(_buffer, forLoopValue);
            _buffer.Add(OpCodes.JUMP);

            var endJumpValue = _buffer.Count;

            PatchInt32(_buffer, successJumpIndex + 2, successJumpValue);
            PatchInt32(_buffer, exitJumpIndex + 2, endJumpValue);
            PatchInt32(_buffer, lteExitJumpIndex + 2, endJumpValue);
            foreach (var index in exitStatementIndexes) PatchInt32(_buffer, index + 2, endJumpValue);
            foreach (var index in skipStatementIndexes) PatchInt32(_buffer, index + 2, stepLoopValue);
            ReturnJumpList(exitStatementIndexes);
            ReturnJumpList(skipStatementIndexes);
        }
         
        
        private void Compile(DoLoopStatement doLoopStatement)
        {
            // first, keep track of the start of the while loop
            var whileLoopValue = _buffer.Count;
            
            // keep track of the first index of the success
            var successJumpValue = _buffer.Count;
            _exitInstructionIndexes.Push(RentJumpList());
            _skipInstructionIndexes.Push(RentJumpList());
            foreach (var successStatement in doLoopStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();
            var skipStatementIndexes = _skipInstructionIndexes.Pop();

            // at the end of the successful statements, we need to jump back to the start
            AddPushInt(_buffer, whileLoopValue);
            _buffer.Add(OpCodes.JUMP);

            var endJumpValue = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);

            foreach (var index in exitStatementIndexes) PatchInt32(_buffer, index + 2, endJumpValue);
            foreach (var index in skipStatementIndexes) PatchInt32(_buffer, index + 2, whileLoopValue);
            ReturnJumpList(exitStatementIndexes);
            ReturnJumpList(skipStatementIndexes);
        }
        
        
        private void Compile(RepeatUntilStatement repeatStatement)
        {
            // first, keep track of the start of the while loop
            var startValue = _buffer.Count;
            
            _exitInstructionIndexes.Push(RentJumpList());
            _skipInstructionIndexes.Push(RentJumpList());

            foreach (var successStatement in repeatStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();
            var skipStatementIndexes = _skipInstructionIndexes.Pop();

            // keep track of where the skip should go
            var skipJumpValue = _buffer.Count;

            Compile(repeatStatement.condition);
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(TypeCodes.INT);
            _buffer.Add(OpCodes.NOT);
            AddPushInt(_buffer, startValue);
            _buffer.Add(OpCodes.JUMP_GT_ZERO);

            var endJumpValue = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);

            foreach (var index in exitStatementIndexes) PatchInt32(_buffer, index + 2, endJumpValue);
            foreach (var index in skipStatementIndexes) PatchInt32(_buffer, index + 2, skipJumpValue);
            ReturnJumpList(exitStatementIndexes);
            ReturnJumpList(skipStatementIndexes);
        }
        
        
        private void Compile(WhileStatement whileStatement)
        {
            /*
             * Loop:
             * <condition>
             * PUSH addr of success
             * JUMP_GT_ZERO
             * PUSH addr of exit
             * JUMP
             * Success:
             *  positive-statements
             *  JUMP Loop:
             * Exit:
             */
            
            // first, keep track of the start of the while loop
            var whileLoopValue = _buffer.Count;
            
            // compile the condition expression
            Compile(whileStatement.condition);

            // cast the expression to an int
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(TypeCodes.INT);
            
            // then, put a fake value in for the while-statement success jump... We'll fix it later.
            var successJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            
            // then, do the jump-gt-zero
            _buffer.Add(OpCodes.JUMP_GT_ZERO);
            
            // if we didn't jump, then we need to load exit the while loop
    
            var exitJumpIndex = _buffer.Count;
            // and then jump to the exit block
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
            
            // keep track of the first index of the success
            var successJumpValue = _buffer.Count;
            _exitInstructionIndexes.Push(RentJumpList());
            _skipInstructionIndexes.Push(RentJumpList());
            foreach (var successStatement in whileStatement.statements)
            {
                Compile(successStatement);
            }
            var exitStatementIndexes = _exitInstructionIndexes.Pop();
            var skipStatementIndexes = _skipInstructionIndexes.Pop();

            // at the end of the successful statements, we need to jump back to the start
            AddPushInt(_buffer, whileLoopValue);
            _buffer.Add(OpCodes.JUMP);

            var endJumpValue = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);

            PatchInt32(_buffer, successJumpIndex + 2, successJumpValue);
            PatchInt32(_buffer, exitJumpIndex + 2, endJumpValue);
            foreach (var index in exitStatementIndexes) PatchInt32(_buffer, index + 2, endJumpValue);
            foreach (var index in skipStatementIndexes) PatchInt32(_buffer, index + 2, whileLoopValue);
            ReturnJumpList(exitStatementIndexes);
            ReturnJumpList(skipStatementIndexes);
        }
        
        private void Compile(IfStatement ifStatement)
        {
            /*
             * <condition value>
             * PUSH addr of Success:
             * JUMP_GT_ZERO
             * PUSH addr of Else
             * JUMP
             * Success:
             *  positive-if-statements
             *  JUMP Final:
             * Else:
             *  else-if-statements
             * Final:
             */
            
            // first, compile the evaluation of the condition
            Compile(ifStatement.condition);
            
            // cast the expression to an int
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(TypeCodes.INT);
            
            // then, put a fake value in for the if-statement success jump... We'll fix it later.
            var successJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            
            // then, do the jump-gt-zero
            _buffer.Add(OpCodes.JUMP_GT_ZERO);
            
            // if we didn't jump, then we need to load up the ELSE block
            var elseJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);

            // and then jump to the else block
            _buffer.Add(OpCodes.JUMP);

            // now it is time to start compiling the actual statements...
            
            // keep track of the first index of the success
            var successJumpValue = _buffer.Count;

            foreach (var successStatement in ifStatement.positiveStatements)
            {
                Compile(successStatement);
            }

            _dbg?.AddFakeDebugToken(_buffer.Count - 1, ifStatement.endToken);
            
            // at the end of the successful statements, we need to jump to the end
            var endJumpIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);


            // this is where the else statements begin
            var elseJumpValue = _buffer.Count;
            // _buffer.Add(OpCodes.NOOP);
            foreach (var elseStatement in ifStatement.negativeStatements)
            {
                Compile(elseStatement);
            }

            // _dbg?.AddFakeDebugToken(_buffer.Count - 1, ifStatement.endToken);

            var endJumpValue = _buffer.Count;
            // _buffer.Add(OpCodes.NOOP);
            
            PatchInt32(_buffer, successJumpIndex + 2, successJumpValue);
            PatchInt32(_buffer, elseJumpIndex + 2, elseJumpValue);
            PatchInt32(_buffer, endJumpIndex + 2, endJumpValue);
        }
        
        private void Compile(LabelDeclarationNode labelStatement)
        {
            // take note of instruction number...
            _labelToInstructionIndex[MakeLabelKey(_currentLabelRegion, labelStatement.label)] = _buffer.Count;
            _buffer.Add(OpCodes.NOOP);
            // Emit RUNTO_YIELD only for labels that some test targets via `runto`.
            // In `dotnet run` builds where no tests exist, this set is empty and
            // there is zero per-label overhead.
            if (_runtoTargetLabels.Contains(labelStatement.label))
            {
                _buffer.Add(OpCodes.RUNTO_YIELD);
            }
        }

        private void Compile(RuntoStatement runtoStatement)
        {
            // Stack at RUNTO dispatch: [..., maxCycles, target]. Target is on top
            // so the VM's existing pop order is preserved. Absent `max cycles`
            // clause -> push int.MaxValue as the unbounded sentinel.
            if (runtoStatement.maxCyclesExpression != null)
            {
                Compile(runtoStatement.maxCyclesExpression);
            }
            else
            {
                AddPushInt(_buffer, int.MaxValue);
            }

            // Target placeholder, patched in CompileJumpReplacements with the
            // post-yield address (label_addr + 2).
            _runtoReplacements.Add(new LabelReplacement
            {
                InstructionIndex = _buffer.Count,
                Label = runtoStatement.targetLabel
            });
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.RUNTO);
        }

        private void Compile(AssertStatement assertStatement)
        {
            // Layout:
            //   <evaluate condition>      ; pushes int (0 = false, !0 = true)
            //   PUSH int <skipAddr>       ; placeholder for skip-on-pass target
            //   JUMP_GT_ZERO              ; if value > 0, jump past failure block
            //   <evaluate reason expr>    ; short-circuit: only runs on failure
            //   <push source-text string ptr>
            //   PUSH int <trampolineAddr> ; address the VM jumps to in test mode
            //   ASSERT_FAIL               ; pops trampoline addr, source text, reason
            //   :skipAddr (continue normally)

            Compile(assertStatement.condition);

            // Placeholder for the skip address; we patch it after emitting the
            // failure branch so we know where the post-failure code starts.
            var skipAddrIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP_GT_ZERO);

            // Failure branch — only reached when the condition is zero. Because
            // this comes after JUMP_GT_ZERO, the reason expression is evaluated
            // lazily: a side-effecting reason (a function call, etc.) only runs
            // when the assertion actually fails.
            //
            // Push the optional reason string first so it sits below the source
            // text on the stack, then push the source text, then fail. Literal
            // strings are interned via the standard literal-string compile path;
            // variable references compile to a heap-pointer push.
            if (assertStatement.reason != null)
            {
                Compile(assertStatement.reason);
            }
            else
            {
                Compile(new LiteralStringExpression(assertStatement.startToken, ""));
            }
            Compile(new LiteralStringExpression(assertStatement.startToken, assertStatement.sourceText ?? ""));

            // Push the trampoline address as a placeholder; ASSERT_FAIL reads it
            // off the stack and (in test mode) jumps there to drain defers. We
            // patch the real address after the trampoline is emitted.
            var trampolinePatchIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _assertTrampolinePatches.Add(trampolinePatchIndex);

            _buffer.Add(OpCodes.ASSERT_FAIL);

            // Patch the skip address to point at the byte right after the failure
            // branch. AddPushInt emits 2 prefix bytes (opcode + type) before the
            // 4-byte int payload, so the int value lives at skipAddrIndex+2.
            PatchInt32(_buffer, skipAddrIndex + 2, _buffer.Count);
        }

        /// <summary>
        /// Emit the one-time "assert unwind trampoline" used when an assert
        /// fails inside a test. The trampoline drains the current scope's
        /// defers (LIFO), then walks up the scope stack, draining each scope's
        /// defers in turn, until only the global scope is left. Then it halts.
        ///
        /// All assert failure sites push the trampoline's address onto the
        /// stack and ASSERT_FAIL (in test mode) sets instructionIndex to it.
        /// In non-test mode ASSERT_FAIL discards the address and crashes the
        /// VM via TriggerRuntimeError instead, so the trampoline never runs.
        /// </summary>
        private void CompileAssertUnwindTrampoline()
        {
            // Skip emission entirely if no assert sites exist.
            if (_assertTrampolinePatches.Count == 0) return;

            var trampolineStart = _buffer.Count;

            // ── Drain loop for the current scope's defers ────────────────
            // Mirrors HandleDeferExit, but the return address pushed onto the
            // data stack is `trampolineStart` itself, so a deferred body
            // returns here and we pop the next defer.
            AddPushInt(_buffer, trampolineStart);
            _buffer.Add(OpCodes.POP_DEFER);   // pushes addr or 0
            _buffer.Add(OpCodes.DUPE);
            var drainEndPatchIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP_ZERO);   // if addr==0, jump out of drain
            _buffer.Add(OpCodes.JUMP);        // else jump to defer body

            // ── after_drain: defer stack for current scope is empty ──────
            var afterDrainAddr = _buffer.Count;
            // Stack here is [trampolineStart, 0] from the JUMP_ZERO path.
            _buffer.Add(OpCodes.DISCARD_TYPED);
            _buffer.Add(OpCodes.DISCARD_TYPED);

            // Decide whether to pop another scope. Halt when only global is left.
            _buffer.Add(OpCodes.PUSH_SCOPE_DEPTH);   // depth
            AddPushInt(_buffer, 1);                  // 1
            _buffer.Add(OpCodes.GT);                 // pushes (depth > 1) ? 1 : 0

            var popAndLoopPatchIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);       // address placeholder
            _buffer.Add(OpCodes.JUMP_GT_ZERO);       // if depth > 1, loop back via pop_and_loop

            // ── halt: only global scope remains; halt VM via overshoot ───
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);

            // ── pop_and_loop: pop one scope, jump back to trampolineStart ─
            var popAndLoopAddr = _buffer.Count;
            _buffer.Add(OpCodes.POP_SCOPE);
            AddPushInt(_buffer, trampolineStart);
            _buffer.Add(OpCodes.JUMP);

            // Back-patch the two forward references inside the trampoline.
            PatchAddress(drainEndPatchIndex, afterDrainAddr);
            PatchAddress(popAndLoopPatchIndex, popAndLoopAddr);

            // Back-patch every assert site's trampoline-address placeholder.
            foreach (var siteIndex in _assertTrampolinePatches)
            {
                PatchAddress(siteIndex, trampolineStart);
            }
        }

        // Helper: write a 4-byte int into the body of an `AddPushInt(buffer, _)`
        // placeholder (which lays out [opcode, typecode, b0, b1, b2, b3]).
        private void PatchAddress(int placeholderIndex, int value)
        {
            PatchInt32(_buffer, placeholderIndex + 2, value);
        }

        // Emit CALL_COUNT with an inline 4-byte command id, pushing that
        // command's invocation count onto the data stack.
        private void EmitCallCountInline(int commandId)
        {
            _buffer.Add(OpCodes.CALL_COUNT);
            AppendInt32(_buffer, commandId);
        }

        private void Compile(ReturnStatement _)
        {
            _buffer.Add(OpCodes.RETURN);
        }

        private List<int> ResolveMockCommandIds(string commandName)
        {
            // A mock targets every overload sharing the given name. Iterate the
            // method table (which includes every overload) and gather the ids
            // of those whose name matches case-insensitively.
            var ids = new List<int>();
            var methods = methodTable.methods;
            for (var i = 0; i < methods.Length; i++)
            {
                if (string.Equals(methods[i].name, commandName,
                        StringComparison.OrdinalIgnoreCase))
                {
                    ids.Add(i);
                }
            }
            return ids;
        }

        private void Compile(MockStatement mockStatement)
        {
            var allCommandIds = ResolveMockCommandIds(mockStatement.commandName);
            if (allCommandIds.Count == 0)
            {
                // Unknown command — the lexer would normally have caught this
                // (CommandWord token doesn't form). Skip silently here.
                return;
            }

            // Filter to overloads whose non-VmArg arg count matches the
            // user-named param count. When the user gives zero names, the
            // mock applies to every overload (the body's prelude pops every
            // arg via DISCARD_TYPED, so any arg count is handled).
            //
            // Filtering is necessary because a single mock body's prelude
            // is tied to one specific overload's signature — different
            // overloads with different arg counts need different prelude
            // bytecode, which is what the per-overload loop below emits.
            var matchingIds = new List<int>();
            foreach (var id in allCommandIds)
            {
                if (mockStatement.parameters.Count == 0)
                {
                    matchingIds.Add(id);
                    continue;
                }
                var methodArgs = methodTable.methods[id].args ?? System.Array.Empty<CommandArgInfo>();
                var realCount = 0;
                for (var ai = 0; ai < methodArgs.Length; ai++)
                {
                    if (!methodArgs[ai].isVmArg) realCount++;
                }
                if (realCount == mockStatement.parameters.Count)
                {
                    matchingIds.Add(id);
                }
            }
            if (matchingIds.Count == 0)
            {
                // The visitor surfaces this as a validation error; the
                // compiler just bails on emitting any bytecode.
                return;
            }

            // Per-overload: emit a separate body block tailored to that
            // overload's signature, then install it for that overload's
            // method id. Bodies share source statements but get independent
            // register allocations because each body pushes its own
            // CompilePushScope before binding args.
            foreach (var commandId in matchingIds)
            {
                CompileMockBodyForOverload(mockStatement, commandId);
            }
        }

        // Emit one mock-body block + install op for a single overload.
        private void CompileMockBodyForOverload(MockStatement mockStatement, int commandId)
        {
            var argMethod = methodTable.methods[commandId];

            // Skip-over JUMP so normal execution flows past this body.
            var skipBodyPatchIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);

            var bodyStart = _buffer.Count;
            // Register in DebugData so call-stack frames inside this body
            // get a sensible function name (the mocked command's name).
            if (mockStatement.commandNameToken != null)
            {
                _dbg?.AddFunction(bodyStart, mockStatement.commandNameToken);
            }
            CompilePushScope();

            // Build the list of real (non-VmArg) arg indices for this overload.
            var commandArgs = argMethod.args ?? System.Array.Empty<CommandArgInfo>();
            var realArgIndices = new List<int>();
            for (var ai = 0; ai < commandArgs.Length; ai++)
            {
                if (!commandArgs[ai].isVmArg) realArgIndices.Add(ai);
            }

            // Per-ref-param bookkeeping used to emit writebacks at every
            // body exit. Independent per overload.
            var prevRefMap = _activeMockRefBindings;
            _activeMockRefBindings = new List<MockRefBinding>();

            // Args were pushed LIFO; pop in reverse to bind to user-named
            // positions left-to-right. If the user gave no param names,
            // DISCARD_TYPED keeps the stack clean.
            for (var i = realArgIndices.Count - 1; i >= 0; i--)
            {
                var argInfo = commandArgs[realArgIndices[i]];
                var paramIndex = i;
                if (paramIndex < mockStatement.parameters.Count)
                {
                    var paramRef = mockStatement.parameters[paramIndex];
                    if (argInfo.isParams)
                    {
                        // `params object[]` (ANY) named in the mock body
                        // is rejected by the visitor (MockParamsObjectArrayUnnamable)
                        // because the gathered array would need mixed-type
                        // element storage. Skip the binding here so we
                        // don't crash on SIZE_TABLE[ANY]; the visitor's
                        // ParseError is what the user sees.
                        if (argInfo.typeCode == TypeCodes.ANY)
                        {
                            // Drain count + values off the stack so later
                            // bindings line up.
                            _buffer.Add(OpCodes.DISCARD_TYPED); // count
                            // We don't know how many were pushed at compile
                            // time, so we can't drain values without a
                            // loop. Bail — the program won't run correctly,
                            // but the user has the validation error to fix.
                            continue;
                        }
                        // Params arg. The caller pushed `[values..., count]`
                        // on the stack. Materialize a Fade single-dimensional
                        // array from those, bind it to a body-local that the
                        // user can index and pass to `len`. Shape mirrors
                        // `dim xs(N)` so existing array-access machinery
                        // works without further changes.
                        var paramsArrayVar = scope.CreateArray(
                            paramRef.variableName, rankLength: 1,
                            typeCode: argInfo.typeCode, isGlobal: false);
                        // CreateArray just sizes the rank-arrays; we still
                        // need to allocate distinct register slots for the
                        // rank size and scaler — mirrors what the regular
                        // `dim` codegen does (Compile(DeclarationStatement)).
                        paramsArrayVar.rankSizeRegisterAddresses[0] = scope.AllocateRegister();
                        paramsArrayVar.rankIndexScalerRegisterAddresses[0] = scope.AllocateRegister();

                        // 1) DUPE the count so we can stash it in the
                        //    rank-size register before GATHER consumes it.
                        _buffer.Add(OpCodes.DUPE);
                        PushStore(_buffer, paramsArrayVar.rankSizeRegisterAddresses[0], isGlobal: false);

                        // 2) Rank-0 scaler is 1 for a 1-D array.
                        AddPushInt(_buffer, 1);
                        PushStore(_buffer, paramsArrayVar.rankIndexScalerRegisterAddresses[0], isGlobal: false);

                        // 3) GATHER pops count + values and leaves a fresh
                        //    PTR_HEAP on top. Store it into the array's
                        //    main register.
                        _buffer.Add(OpCodes.GATHER_ARRAY);
                        _buffer.Add(argInfo.typeCode);
                        PushStorePtr(_buffer, paramsArrayVar.registerAddress, isGlobal: false);
                        continue;
                    }
                    if (argInfo.isRef)
                    {
                        // Ref param. Hidden ptr reg + user-visible value reg.
                        var hiddenPtrName = "$$mockptr_" + paramRef.variableName;
                        var hiddenRef = new VariableRefNode(paramRef.startToken, hiddenPtrName);
                        var hiddenDecl = new DeclarationStatement
                        {
                            variableNode = hiddenRef,
                            scopeType = DeclarationScopeType.Local,
                            type = new TypeReferenceNode(VariableType.Integer, paramRef.startToken)
                        };
                        Compile(hiddenDecl);
                        scope.TryGetVariable(hiddenPtrName, out var hiddenPtrVar);

                        _buffer.Add(OpCodes.STORE_REF);
                        AddPushULongNoTypeCode(_buffer, hiddenPtrVar.registerAddress);

                        VmUtil.TryGetVariableType(argInfo.typeCode, out var valueType);
                        var valueDecl = new DeclarationStatement
                        {
                            variableNode = paramRef,
                            scopeType = DeclarationScopeType.Local,
                            type = new TypeReferenceNode(valueType, paramRef.startToken)
                        };
                        Compile(valueDecl);
                        scope.TryGetVariable(paramRef.variableName, out var valueVar);

                        _buffer.Add(OpCodes.LOAD_REF);
                        AddPushULongNoTypeCode(_buffer, hiddenPtrVar.registerAddress);
                        _buffer.Add(OpCodes.CAST);
                        _buffer.Add(argInfo.typeCode);
                        CompileAssignmentLeftHandSide(paramRef);

                        _activeMockRefBindings.Add(new MockRefBinding
                        {
                            paramName = paramRef.variableName,
                            valueRegAddr = valueVar.registerAddress,
                            ptrRegAddr = hiddenPtrVar.registerAddress,
                            argTypeCode = argInfo.typeCode
                        });
                    }
                    else
                    {
                        VmUtil.TryGetVariableType(argInfo.typeCode, out var paramType);
                        var fakeDecl = new DeclarationStatement
                        {
                            variableNode = paramRef,
                            scopeType = DeclarationScopeType.Local,
                            type = new TypeReferenceNode(paramType, paramRef.startToken)
                        };
                        Compile(fakeDecl);
                        _buffer.Add(OpCodes.CAST);
                        _buffer.Add(argInfo.typeCode);
                        CompileAssignmentLeftHandSide(paramRef);
                    }
                }
                else
                {
                    _buffer.Add(OpCodes.DISCARD_TYPED);
                }
            }

            // Active-mock context for nested compile of body statements.
            var prevReturnTc = _activeMockReturnTypeCode;
            var prevCmdName = _activeMockCommandName;
            var prevHostId = _activeMockHostMethodId;
            var prevParamBindings = _activeMockParamBindings;
            var prevBypassIds = _activeMockBypassIds;
            _activeMockReturnTypeCode = argMethod.returnType;
            _activeMockCommandName = argMethod.name;
            _activeMockHostMethodId = commandId;
            // All overloads of the mocked command name route to real
            // inside this body — gather their ids once. Compile of
            // CommandStatement/CommandExpression checks this set.
            _activeMockBypassIds = new HashSet<int>(
                ResolveMockCommandIds(mockStatement.commandName));

            // Build the ordered param-binding table used by
            // PassthroughExpression: one entry per real (non-VmArg) arg,
            // in declaration order, paired with the mock's body-local
            // name (null when the mock didn't name that position).
            _activeMockParamBindings = new List<MockParamBinding>();
            for (var ri = 0; ri < realArgIndices.Count; ri++)
            {
                var argInfo = commandArgs[realArgIndices[ri]];
                var paramName = (ri < mockStatement.parameters.Count)
                    ? mockStatement.parameters[ri].variableName
                    : null;
                _activeMockParamBindings.Add(new MockParamBinding
                {
                    paramName = paramName,
                    argTypeCode = argInfo.typeCode,
                    isRef = argInfo.isRef,
                    isParams = argInfo.isParams
                });
            }

            foreach (var stmt in mockStatement.body)
            {
                Compile(stmt);
            }

            // endmock <expr> fall-through return value.
            if (mockStatement.endmockExpression != null)
            {
                Compile(mockStatement.endmockExpression);
                if (_activeMockReturnTypeCode != 0 && _activeMockReturnTypeCode != TypeCodes.VOID)
                {
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(_activeMockReturnTypeCode);
                }
            }

            // Ref-arg writebacks before scope-pop (still need the binding map).
            EmitMockRefWritebacks();

            _activeMockReturnTypeCode = prevReturnTc;
            _activeMockCommandName = prevCmdName;
            _activeMockRefBindings = prevRefMap;
            _activeMockHostMethodId = prevHostId;
            _activeMockParamBindings = prevParamBindings;
            _activeMockBypassIds = prevBypassIds;

            CompilePopScope();
            _buffer.Add(OpCodes.RETURN);

            // Patch the skip-over JUMP to land past this body.
            PatchAddress(skipBodyPatchIndex, _buffer.Count);

            // Install the body for this specific overload's id.
            AddPushInt(_buffer, bodyStart);
            AddPushInt(_buffer, commandId);
            _buffer.Add(OpCodes.MOCK_INSTALL);
        }

        // The active mock's return-type code, set while compiling a mock
        // body. MockExitMockStatement reads this to cast the user's return
        // expression to the right shape before pushing it on the stack and
        // returning. Outside a mock-body compile, this is 0 (VOID).
        private byte _activeMockReturnTypeCode;

        private void Compile(MockExitMockStatement returnsStatement)
        {
            // `exitmock expr` inside a mock body: push the value, cast it to
            // the command's declared return type, emit ref-arg writebacks,
            // pop the body's scope and RETURN. The writebacks read each
            // ref param's value-register (last write the user did) and
            // store it back to the caller's variable via the saved ptr.
            if (returnsStatement.expression != null)
            {
                Compile(returnsStatement.expression);
                if (_activeMockReturnTypeCode != 0 && _activeMockReturnTypeCode != TypeCodes.VOID)
                {
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(_activeMockReturnTypeCode);
                }
            }
            EmitMockRefWritebacks();
            CompilePopScope();
            _buffer.Add(OpCodes.RETURN);
        }

        private void Compile(MockForbidStatement forbidStatement)
        {
            // `forbid [reason]` inside a mock body: shape-compatible with
            // ASSERT_FAIL. The body is already running inside a pushed scope
            // (set up by the mock body prelude); the trampoline drains
            // defers across every live scope and halts the test.
            //
            // Stack at ASSERT_FAIL (bottom→top):
            //   reason, sourceText, trampolineAddr.
            if (forbidStatement.reason != null)
            {
                Compile(forbidStatement.reason);
            }
            else
            {
                Compile(new LiteralStringExpression(forbidStatement.startToken, ""));
            }
            // Synthesize a sourceText that names the command being forbidden.
            // Walk up to the enclosing MockStatement for the name; if we
            // somehow have none, fall back to a generic message.
            var cmdName = _activeMockCommandName ?? "<unknown>";
            Compile(new LiteralStringExpression(forbidStatement.startToken,
                "forbidden command was called: " + cmdName));
            var trampolinePatchIndex = _buffer.Count;
            AddPushInt(_buffer, int.MaxValue);
            _assertTrampolinePatches.Add(trampolinePatchIndex);
            _buffer.Add(OpCodes.ASSERT_FAIL);
        }

        // Command name of the mock currently being compiled. Read by
        // MockForbidStatement so the failure message names the command.
        private string _activeMockCommandName;

        // Per-ref-param bookkeeping for the active mock-body compile.
        // Populated by the body prelude with one entry per ref parameter,
        // then read at every exit site (exitmock, endmock fall-through) to
        // emit the writeback sequence. Empty when the active mock has no
        // ref params (or when we're not inside a mock body).
        private List<MockRefBinding> _activeMockRefBindings;

        // Emit ref writebacks for every ref param in the current mock.
        // Called from each body exit point: pushes nothing net onto the
        // stack (each writeback loads the value reg and consumes it via
        // WRITE_REF). Order is irrelevant — each binding writes to a
        // distinct caller register.
        private void EmitMockRefWritebacks()
        {
            if (_activeMockRefBindings == null) return;
            foreach (var binding in _activeMockRefBindings)
            {
                // Load the value-register's current value.
                PushLoad(_buffer, binding.valueRegAddr, isGlobal: false);
                // CAST to the ref's underlying type — defensive in case the
                // user did any unusual arithmetic that widened the type.
                _buffer.Add(OpCodes.CAST);
                _buffer.Add(binding.argTypeCode);
                // Write through the saved pointer to the caller's register.
                _buffer.Add(OpCodes.WRITE_REF);
                AddPushULongNoTypeCode(_buffer, binding.ptrRegAddr);
            }
        }

        struct MockRefBinding
        {
            public string paramName;
            // Body-local register holding the value the user reads/writes.
            // Typed as the arg's base type (int / float / etc).
            public ulong valueRegAddr;
            // Hidden body-local register holding the typed caller pointer
            // (PTR_REG or PTR_GLOBAL_REG). Used by WRITE_REF at writeback.
            public ulong ptrRegAddr;
            // The command arg's underlying TypeCode (e.g. TypeCodes.INTEGER).
            public byte argTypeCode;
        }

        // Per-arg binding info for the currently-compiling mock body — one
        // entry per real (non-VmArg) command arg, in declaration order.
        // PassthroughExpression iterates these to re-construct the call.
        struct MockParamBinding
        {
            public string paramName;
            public byte argTypeCode;
            public bool isRef;
            public bool isParams;
        }

        // Host-method id of the command currently being mocked. Read by
        // PassthroughExpression to emit the CALL_HOST_REAL target. Zero
        // outside a mock body.
        private int _activeMockHostMethodId;

        // Ordered list of bindings for the active mock body. Index matches
        // the order in which args are pushed at a normal call site (which
        // is also the order CALL_HOST_REAL expects).
        private List<MockParamBinding> _activeMockParamBindings;

        // `passthrough` inside a mock body — re-pushes the body's currently
        // bound argument values, then dispatches to the real underlying
        // command via CALL_HOST_REAL (which bypasses the mock table).
        // Leaves the real command's return value (if any) on the stack;
        // when used as a statement, the wrapping ExpressionStatement
        // emits a DISCARD_TYPED.
        //
        // Args are re-built from body-locals in declaration order:
        //   - value: LOAD <valReg> + CAST <argTc>
        //   - ref:   flush user-side write through hidden ptr (LOAD val,
        //            CAST, WRITE_REF <ptrReg>), then LOAD <ptrReg> so the
        //            real host can read AND write through it.
        //   - params: LOAD_PTR <arrReg> + SPREAD_ARRAY <argTc>.
        //
        // After the call we refresh each ref param's value-reg from the
        // caller (which the real command may have updated) so subsequent
        // body reads see the real output.
        // Set of host-method ids that, when invoked inside the current
        // mock body, should dispatch to the real host (CALL_HOST_REAL)
        // instead of looking up the mock table. Populated per body with
        // every overload id of the mocked command name. Null outside a
        // mock body. Read at the top of Compile(CommandStatement) and
        // Compile(CommandExpression) for the self-recursive rewrite.
        private HashSet<int> _activeMockBypassIds;

        // Compile a CommandStatement or CommandExpression whose target
        // is the mocked command. Emits the same shape as a normal call
        // (push each arg, then PUSH cmd-id, then CALL_HOST_REAL), but
        // ref args route through the body's bound ref-param table so
        // writes land in the caller's scope. After the call, refresh
        // each refreshed ref's value-reg from the caller so later body
        // reads see the real-output. Caller is responsible for emitting
        // the final DISCARD when used as a statement (the regular
        // CommandStatement compile already handles void-return discard).
        private void CompileMockedCommandSelfCall(CommandInfo command,
            List<IExpressionNode> args, int commandId, bool isStatement)
        {
            var refsRefreshed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            var argCounter = 0;
            for (var i = 0; i < command.args.Length; i++)
            {
                var argDesc = command.args[i];
                if (argDesc.isVmArg) continue;

                if (argDesc.isParams)
                {
                    // Spread shape OR inline. Mirror the regular
                    // CommandStatement params logic: if the only
                    // remaining user arg is an array-typed expression,
                    // compile it and SPREAD_ARRAY. Otherwise compile each
                    // inline arg in reverse and push the count.
                    var remaining = args.Count - argCounter;
                    if (remaining == 1
                        && args[argCounter].ParsedType.IsArray
                        && args[argCounter].ParsedType.rank == 1)
                    {
                        Compile(args[argCounter]);
                        _buffer.Add(OpCodes.SPREAD_ARRAY);
                        _buffer.Add(argDesc.typeCode);
                    }
                    else
                    {
                        for (var j = args.Count - 1; j >= argCounter; j--)
                        {
                            Compile(args[j]);
                        }
                        AddPushInt(_buffer, args.Count - argCounter);
                    }
                    break;
                }

                if (argCounter >= args.Count)
                {
                    if (argDesc.isOptional)
                    {
                        AddPush(_buffer, new byte[] { }, TypeCodes.VOID);
                        continue;
                    }
                    throw new Exception(
                        "Compiler: self-recursive mock call missing required arg");
                }

                var userExpr = args[argCounter];

                if (argDesc.isRef)
                {
                    // Visitor already required this to be a bound ref-param
                    // name. Route through the binding so the host writes
                    // into the caller's scope (where the original ref lives).
                    string refName = (userExpr is VariableRefNode vn) ? vn.variableName : null;
                    if (refName == null)
                    {
                        throw new Exception(
                            "Compiler: self-recursive mock ref arg must be a variable ref by validation");
                    }
                    EmitMockedCallRefByBoundName(argDesc.typeCode, refName, refsRefreshed);
                    argCounter++;
                    continue;
                }

                // Value arg — compile the user's expression normally, cast.
                Compile(userExpr);
                if (argDesc.typeCode != TypeCodes.ANY)
                {
                    CompileCast(argDesc.typeCode);
                }
                argCounter++;
            }

            // Push the host method id and dispatch to the real command.
            _buffer.Add(OpCodes.PUSH);
            _buffer.Add(TypeCodes.INT);
            AppendInt32(_buffer, commandId);
            _buffer.Add(OpCodes.CALL_HOST_REAL);

            // Refresh ref bindings that this call wrote through, so later
            // body reads observe the real output. Untouched bindings stay
            // as the user left them (preserves any pre-call user write).
            if (_activeMockRefBindings != null)
            {
                foreach (var rb in _activeMockRefBindings)
                {
                    if (!refsRefreshed.Contains(rb.paramName)) continue;
                    _buffer.Add(OpCodes.LOAD_REF);
                    AddPushULongNoTypeCode(_buffer, rb.ptrRegAddr);
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(rb.argTypeCode);
                    var refNode = new VariableRefNode(null, rb.paramName);
                    CompileAssignmentLeftHandSide(refNode);
                }
            }

            // For void real-commands invoked at statement position there
            // is nothing on the stack to discard. For value-returning
            // ones at statement position, the regular CommandStatement
            // caller doesn't emit a discard either — the value is left
            // on the stack. Match that behavior here (the caller stack
            // hygiene is the same as a normal CALL_HOST).
        }

        // Flush the body-visible value-reg through the hidden ptr (so
        // the real host reads the user's latest write), then push the
        // ptr itself as PTR_REG / PTR_GLOBAL_REG. Records the binding
        // name in `refsRefreshed` so we know which value-regs to reload
        // from the caller after the call.
        private void EmitMockedCallRefByBoundName(byte argTypeCode,
            string boundName, HashSet<string> refsRefreshed)
        {
            MockRefBinding refBinding = default;
            var found = false;
            if (_activeMockRefBindings != null)
            {
                foreach (var rb in _activeMockRefBindings)
                {
                    if (string.Equals(rb.paramName, boundName,
                        StringComparison.OrdinalIgnoreCase))
                    {
                        refBinding = rb;
                        found = true;
                        break;
                    }
                }
            }
            if (!found)
            {
                throw new Exception(
                    "Compiler: self-recursive mock call missing ref binding for " + boundName);
            }
            PushLoad(_buffer, refBinding.valueRegAddr, isGlobal: false);
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(argTypeCode);
            _buffer.Add(OpCodes.WRITE_REF);
            AddPushULongNoTypeCode(_buffer, refBinding.ptrRegAddr);
            PushLoad(_buffer, refBinding.ptrRegAddr, isGlobal: false);
            refsRefreshed.Add(refBinding.paramName);
        }

        private void Compile(ClearMockStatement clearMockStatement)
        {
            if (clearMockStatement.commandName == null)
            {
                _buffer.Add(OpCodes.MOCK_CLEAR_ALL);
                return;
            }

            var commandIds = ResolveMockCommandIds(clearMockStatement.commandName);
            foreach (var commandId in commandIds)
            {
                AddPushInt(_buffer, commandId);
                _buffer.Add(OpCodes.MOCK_CLEAR);
            }
        }

        private void Compile(EndProgramStatement endProgramStatement)
        {
            CompileEnd();
        }

        private void CompileEnd()
        {
            HandleDeferExit();
            // jump to the end of the instruction pointer space, a hack?
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
        }
        
        private void Compile(GoSubStatement goSubStatement)
        {

            _labelReplacements.Add(new LabelReplacement
            {
                InstructionIndex = _buffer.Count,
                Label = MakeLabelKey(_currentLabelRegion, goSubStatement.label)
            });
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP_HISTORY);

        }

        private void Compile(GotoStatement gotoStatement)
        {
            // identify the instruction ID of the label
            _labelReplacements.Add(new LabelReplacement
            {
                InstructionIndex = _buffer.Count,
                Label = MakeLabelKey(_currentLabelRegion, gotoStatement.label)
            });
            AddPushInt(_buffer, int.MaxValue);
            _buffer.Add(OpCodes.JUMP);
        }
        
        private void Compile(AddressExpression expression)
        {
            // we need to find the address of the given expression... 
            switch (expression.variableNode)
            {
                case VariableRefNode refNode:

                    // this is a register address...
                    if (!scope.TryGetVariable(refNode.variableName, out var variable))
                    {
                        var fakeDeclStatement = new DeclarationStatement
                        {
                            startToken = expression.startToken,
                            endToken = expression.endToken,
                            ranks = null,
                            scopeType = DeclarationScopeType.Local,
                            variableNode = refNode,
                            type = new TypeReferenceNode(refNode.DefaultTypeByName, refNode.startToken)
                        };
                        Compile(fakeDeclStatement, includeDefaultInitializer: true);
                        if (!scope.TryGetVariable(refNode.variableName, out variable))
                        {
                            throw new Exception(
                                "Compiler exception: cannot use reference to a variable that does not exist " + refNode);
                        }
                    }

                    switch (variable.typeCode)
                    {
                        default:
                            // anything else is a registry ptr!
                            var regAddr = variable.registerAddress;
                            _buffer.Add(OpCodes.PUSH);
                            _buffer.Add(variable.isGlobal ? TypeCodes.PTR_GLOBAL_REG : TypeCodes.PTR_REG);
                            AddPushULongNoTypeCode(_buffer, regAddr);
                            break;
                    }
               
                    break;
                
                case ArrayIndexReference indexReference:
                    // if we push the address, that isn't good enough, because it is not a register address...
                    // we need to indicate that the value stored in the stack is actually not a registry ptr, but a heap ptr
                    if (!scope.TryGetArray(indexReference.variableName, out var compiledArrayVar))
                    {
                        throw new Exception("Compiler: cannot access array since it not declared" +
                                            indexReference.variableName);
                    }
                    _buffer.Add(OpCodes.BPUSH);
                    _buffer.Add(compiledArrayVar.typeCode);
                    PushAddress(indexReference);
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(TypeCodes.PTR_HEAP);
                    break;
                
                case StructFieldReference fieldRef:

                    switch (fieldRef.left)
                    {
                        case VariableRefNode leftVariable:

                            if (!scope.TryGetVariable(leftVariable.variableName, out var typeCompiledVar))
                            {
                                FakeDeclare(leftVariable, out typeCompiledVar);
                            }

                            if (!_types.TryGetValue(typeCompiledVar.structType, out var type))
                            {
                                throw new Exception("Unknown type reference " + type);
                            }

                            ComputeStructOffsets(type, fieldRef.right, out var readOffset, out var readLength,
                                out var readTypeCode);

                            // push the type-code of the element
                            _buffer.Add(OpCodes.BPUSH);
                            _buffer.Add(readTypeCode);

                            // push the offset
                            {
                                // first, load up the base address 
                                PushLoad(_buffer, typeCompiledVar.registerAddress, typeCompiledVar.isGlobal);
                                
                                // then insert the offset
                                AddPushInt(_buffer, readOffset); // SIZE, <Data>

                                // and add them up
                                _buffer.Add(OpCodes.ADD);
                            }
                            
                            // convert the address to a ptr
                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(TypeCodes.PTR_HEAP);
                            break;
                        case ArrayIndexReference leftArray:

                            // we need to find the address to the array, then add on the offset
                            if (!scope.TryGetArray(leftArray.variableName, out compiledArrayVar))
                            {
                                throw new Exception(
                                    "Compiler: cannot access array since it not declared (left-struct)" +
                                    leftArray.variableName);
                            }
                            
                            ComputeStructOffsets(compiledArrayVar.structType, fieldRef.right, out readOffset, out readLength,
                                out readTypeCode);

                            _buffer.Add(OpCodes.BPUSH);
                            _buffer.Add(compiledArrayVar.typeCode);
                            

                            // push the offset
                            {
                                // first, load up the base address of the array
                                PushAddress(leftArray);
                                
                                // then insert the offset
                                AddPushInt(_buffer, readOffset); // SIZE, <Data>

                                // and add them up
                                _buffer.Add(OpCodes.ADD);
                            }

                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(TypeCodes.PTR_HEAP);
                            break;
                        default:
                            throw new NotImplementedException("structref left- cannot use the address of this expression " + expression);
                            break;
                    }
                    
                    break;
                default:
                    throw new NotImplementedException("cannot use the address of this expression " + expression);
            }
        }

        public void Compile(CommandStatement commandStatement)
        {
            // Inside a mock body, a call to the mocked command itself
            // (any overload) dispatches to the real host via
            // CALL_HOST_REAL — the mock body is transparent to its own
            // command. Ref args route through the body's bound ref-param
            // table (validation enforced this) so writes land in the
            // caller's scope through the scope-swap in CALL_HOST_REAL.
            if (_activeMockBypassIds != null
                && _commandToPtr.TryGetValue(
                    commandStatement.command.UniqueName, out var bypassIdStmt)
                && _activeMockBypassIds.Contains(bypassIdStmt))
            {
                CompileMockedCommandSelfCall(commandStatement.command,
                    commandStatement.args, bypassIdStmt, isStatement: true);
                return;
            }

            // TODO: save local state?
            // put each expression on the stack.
            var argCounter = 0;
            for (var i = 0; i < commandStatement.command.args.Length; i++)
            {
                if (commandStatement.command.args[i].isVmArg) continue;

                if (commandStatement.command.args[i].isParams)
                {
                    // Spread shape: exactly one remaining arg, and it's an
                    // array-typed expression matching the params element
                    // type. Compile the array (which puts its heap ptr on
                    // the stack), then SPREAD_ARRAY pushes each element +
                    // count — the same shape as the inline loop below.
                    var remaining = commandStatement.args.Count - argCounter;
                    if (remaining == 1
                        && commandStatement.args[argCounter].ParsedType.IsArray
                        && commandStatement.args[argCounter].ParsedType.rank == 1)
                    {
                        Compile(commandStatement.args[argCounter]);
                        _buffer.Add(OpCodes.SPREAD_ARRAY);
                        // Use the array's actual element type, not the
                        // descriptor's — for `params object[]` (TypeCodes.ANY)
                        // the descriptor doesn't carry a usable byte size,
                        // but the source array always has a concrete element
                        // type the VM can size and tag per-element.
                        var descTc = commandStatement.command.args[i].typeCode;
                        var spreadTc = descTc == TypeCodes.ANY
                            ? VmUtil.GetTypeCode(commandStatement.args[argCounter].ParsedType.type)
                            : descTc;
                        _buffer.Add(spreadTc);
                        break;
                    }

                    // Inline-list shape (existing): compile each arg in
                    // reverse, then push the count.
                    for (var j = commandStatement.args.Count - 1; j >= argCounter; j --)
                    {
                        var argExpr2 = commandStatement.args[j];
                        Compile(argExpr2);
                    }

                    // first, we need to tell the program how many arguments there are left in the set
                    // , which of course, is args - i.
                    AddPushInt(_buffer, commandStatement.args.Count - argCounter);
                    break;
                }
                
                if (argCounter >= commandStatement.args.Count)
                {
                    if (commandStatement.command.args[i].isOptional)
                    {
                        AddPush(_buffer, new byte[]{}, TypeCodes.VOID);
                        continue;
                    }
                    else
                    {
                        throw new Exception("Compiler: not enough arg expressions to meet the needs of the function");
                    }
                }
                
                var argExpr = commandStatement.args[argCounter];
                var argDesc = commandStatement.command.args[i];
                if (argDesc.isRef)
                {
                    var argAddr = argExpr as AddressExpression;

                    if (argAddr == null)
                    {

                        if (argExpr is IVariableNode v)
                        {
                            argAddr = new AddressExpression(v, argExpr.StartToken);
                        }
                        else
                        {
                            throw new Exception(
                                "Compiler exception: cannot use a ref parameter with an expr that isn't an address expr");
                        }
                        
                    }
                       
                    Compile(argAddr);
                }
                else
                {
                    Compile(argExpr);

                    var destinationTypeCode =
                        commandStatement.command.args[commandStatement.argMap[argCounter]].typeCode;
                    if (destinationTypeCode != TypeCodes.ANY)
                    {
                        // only cast the type if it isn't the catch-all "any"
                        CompileCast(destinationTypeCode);
                    }
                }
                argCounter++;
            }
            
            
            // find the address of the method
            if (!_commandToPtr.TryGetValue(commandStatement.command.UniqueName, out var commandAddress))
            {
                throw new Exception("compiler: could not find method address: " + commandStatement.command);
            }
            
            _buffer.Add(OpCodes.PUSH);
            _buffer.Add(TypeCodes.INT);
            AppendInt32(_buffer, commandAddress);

            _buffer.Add(OpCodes.CALL_HOST);

        }

        public void Compile(RedimStatement redimStatement)
        {
            if (!scope.TryGetArray(redimStatement.variable.variableName, out var arrayVar))
            {
                throw new Exception("invalid array to redim");
            }

            // need to reset the registers
            for (var i = redimStatement.ranks.Length - 1; i >= 0; i--)
            {
                // put the expression value onto the stack
                var expr = redimStatement.ranks[i];
                Compile(expr);

                // store the expression value (the length for this rank) in a register
                PushStore(_buffer, arrayVar.rankSizeRegisterAddresses[i], arrayVar.isGlobal);


                if (i == redimStatement.ranks.Length - 1)
                {
                    // push 1 as the multiplier factor, because later, multiplying by 1 is a no-op;
                    AddPushInt(_buffer, 1);
                }
                else
                {
                    // get the length of the right term
                    PushLoad(_buffer, arrayVar.rankSizeRegisterAddresses[i + 1], arrayVar.isGlobal);

                    // and get the multiplier factor of the right term
                    PushLoad(_buffer, arrayVar.rankIndexScalerRegisterAddresses[i + 1], arrayVar.isGlobal);

                    // and multiply those together...
                    _buffer.Add(OpCodes.MUL);
                }

                // _buffer.Add(OpCodes.STORE);
                // _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i]); // store the multiplier 
                PushStore(_buffer, arrayVar.rankIndexScalerRegisterAddresses[i], arrayVar.isGlobal);

                // need to clear the data
                
            }
            
            
                
            // now, we need to allocate enough memory for the entire thing
            AddPushInt(_buffer, 1);
                
            for (var i = 0; i < redimStatement.ranks.Length; i++)
            {
                // _buffer.Add(OpCodes.LOAD);
                // _buffer.Add(arrayVar.rankSizeRegisterAddresses[i]); // store the length of the sub var on the register.
                PushLoad(_buffer, arrayVar.rankSizeRegisterAddresses[i], arrayVar.isGlobal);

                _buffer.Add(OpCodes.MUL);
            }
                
            var sizeOfElement = arrayVar.byteSize;
            AddPushInt(_buffer, sizeOfElement);
                
            _buffer.Add(OpCodes.MUL); // multiply the length by the size, to get the entire byte-size of the requested array
                
            // inject the type format.
            var tf = new HeapTypeFormat
            {
                typeCode = arrayVar.typeCode,
                typeId = arrayVar.structType?.typeId ?? 0,
                typeFlags = HeapTypeFormat.CreateArrayFlag(redimStatement.ranks.Length)
            };
            AddPushTypeFormat(_buffer, ref tf);
                
            _buffer.Add(OpCodes.ALLOC); // push the alloc instruction
                
            // _buffer.Add(OpCodes.STORE);
            // _buffer.Add(arrayVar.registerAddress);
            PushStorePtr(_buffer, arrayVar.registerAddress, arrayVar.isGlobal);
        }

        public void Compile(DeclarationStatement declaration, bool includeDefaultInitializer=false)
        {
            /*
             * the declaration tells us that we need a register
             */
            // then, we need to reserve a register for the variable.
            var tc = VmUtil.GetTypeCode(declaration.type.variableType);

          
            
            if (declaration.ranks == null || declaration.ranks.Length == 0)
            {
                // this is a normal variable decl.
                // scope.Create(declaration.variable, tc);
                var compiledVar = scope.Create(declaration.variable, tc, declaration.scopeType == DeclarationScopeType.Global);
                
                if (tc == TypeCodes.STRUCT)
                {

                    switch (declaration.type)
                    {
                        case StructTypeReferenceNode structTypeNode:

                            if (!_types.TryGetValue(structTypeNode.variableNode.variableName, out var structType))
                            {
                                throw new Exception("Compiler: unknown type ref " + structTypeNode.variableNode);
                            }

                            // save the type information on the variable, for lookup later.
                            compiledVar.structType = structTypeNode.variableNode.variableName;

                            // we need to allocate some memory for this instance!
                            AddPushInt(_buffer, structType.byteSize);
                            
                            // create the type-format for the allocation
                            var tf = new HeapTypeFormat
                            {
                                typeCode = TypeCodes.STRUCT,
                                typeFlags = 0,
                                typeId = _types[compiledVar.structType].typeId
                            };
                            AddPushTypeFormat(_buffer, ref tf);
                            
                            // call alloc, which expects to find the length on the stack, and the ptr is returned.
                            _buffer.Add(OpCodes.ALLOC);
                    
                            // cast the ptr to a struct type-code
                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(TypeCodes.STRUCT);
                            
                            // the ptr will be stored in the register for this variable
                            PushStorePtr(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                            // PushStore(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                            _dbg?.AddVariable(_buffer.Count - 1, compiledVar);

                            break;
                
                        default:
                            throw new Exception("compiler cannot handle non struct type ref ");
                    }
  
                }
            }
            else
            {
                // this is an array decl

                var arrayVar = scope.CreateArray(declaration.variable, declaration.ranks.Length, tc, declaration.scopeType == DeclarationScopeType.Global);

                if (tc == TypeCodes.STRUCT)
                {
                    // ah, the byteSize is _NOT_ just the size of the element, it is the size of the struct!
                    var arrayStructRefNode = declaration.type as StructTypeReferenceNode;
                    if (arrayStructRefNode == null) throw new Exception("array struct needs correct node type");
                    var typeName = arrayStructRefNode.variableNode.variableName;
                    if (!_types.TryGetValue(typeName, out var structType))
                    {
                        throw new Exception("Compiler: unknown type ref " + typeName);
                    }

                    arrayVar.byteSize = structType.byteSize;
                    arrayVar.structType = structType;
                }
                
                // this is an array! we need to save each rank's length
                // for (var i = 0; i < declaration.ranks.Length; i++)
                for (var i = declaration.ranks.Length -1; i >= 0; i--)
                {
                    // put the expression value onto the stack
                    var expr = declaration.ranks[i];
                    Compile(expr); 
                    
                    // reserve 2 registers for array rank metadata
                    arrayVar.rankSizeRegisterAddresses[i] = scope.AllocateRegister(); // (byte)(registerCount++);
                    arrayVar.rankIndexScalerRegisterAddresses[i] = scope.AllocateRegister(); //(byte)(registerCount++);
                    
                    // store the expression value (the length for this rank) in a register
                    // _buffer.Add(OpCodes.STORE);
                    // _buffer.Add(arrayVar.rankSizeRegisterAddresses[i]);
                    PushStore(_buffer, arrayVar.rankSizeRegisterAddresses[i], arrayVar.isGlobal);

                    if (i == declaration.ranks.Length - 1)
                    {
                        // push 1 as the multiplier factor, because later, multiplying by 1 is a no-op;
                        AddPushInt(_buffer, 1);
                    }
                    else
                    {
                        // get the length of the right term
                        // _buffer.Add(OpCodes.LOAD);
                        // _buffer.Add(arrayVar.rankSizeRegisterAddresses[i + 1]); 
                        PushLoad(_buffer, arrayVar.rankSizeRegisterAddresses[i + 1], arrayVar.isGlobal);
                        
                        // and get the multiplier factor of the right term
                        // _buffer.Add(OpCodes.LOAD);
                        // _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i + 1]); 
                        PushLoad(_buffer, arrayVar.rankIndexScalerRegisterAddresses[i + 1], arrayVar.isGlobal);


                        // and multiply those together...
                        _buffer.Add(OpCodes.MUL);
                    }
                    
                    // _buffer.Add(OpCodes.STORE);
                    // _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i]); // store the multiplier 
                    PushStore(_buffer, arrayVar.rankIndexScalerRegisterAddresses[i], arrayVar.isGlobal);
                }
                
                
                
                // now, we need to allocate enough memory for the entire thing
                AddPushInt(_buffer, 1);
                
                for (var i = 0; i < declaration.ranks.Length; i++)
                {
                    // _buffer.Add(OpCodes.LOAD);
                    // _buffer.Add(arrayVar.rankSizeRegisterAddresses[i]); // store the length of the sub var on the register.
                    PushLoad(_buffer, arrayVar.rankSizeRegisterAddresses[i], arrayVar.isGlobal);

                    _buffer.Add(OpCodes.MUL);
                }
                
                var sizeOfElement = arrayVar.byteSize;
                AddPushInt(_buffer, sizeOfElement);
                
                _buffer.Add(OpCodes.MUL); // multiply the length by the size, to get the entire byte-size of the requested array
                
                // inject the type format.
                var tf = new HeapTypeFormat
                {
                    typeCode = arrayVar.typeCode,
                    typeId = arrayVar.structType?.typeId ?? 0,
                    typeFlags = HeapTypeFormat.CreateArrayFlag(declaration.ranks.Length)
                };
                AddPushTypeFormat(_buffer, ref tf);
                
                _buffer.Add(OpCodes.ALLOC); // push the alloc instruction
                
                // _buffer.Add(OpCodes.STORE);
                // _buffer.Add(arrayVar.registerAddress);
                PushStorePtr(_buffer, arrayVar.registerAddress, arrayVar.isGlobal);
                _dbg?.AddVariable(_buffer.Count - 1, arrayVar);

            }
            
            
            // later in this compiler, when we find the variable assignment, we'll know where to find it.

            // but we do not actually need to emit any code at this point.

            if (declaration.initializerExpression != null)
            {
                // ah, there is an implicit assignment!
                // we can fake this by creating a fake-assignment node
                var fakeAssignment = new AssignmentStatement
                {
                    expression = declaration.initializerExpression,
                    variable = new VariableRefNode(declaration.startToken, declaration.variable)
                };
                Compile(fakeAssignment);
            }
            else if (includeDefaultInitializer)
            {

                if (declaration.ranks == null)
                {
                    switch (tc)
                    {
                        case TypeCodes.STRUCT:
                            // TODO: it isn't possible to pass structs as refs atm, but if it was, this would be a problem. 
                            break;
                        case TypeCodes.STRING: // TODO: handle the empty string?
                            // if the variable is a string, then always assign it to the empty string, which will get interned. 
                            var fadeStrAssignment = new AssignmentStatement
                            {
                                expression = new LiteralStringExpression(declaration.startToken, ""),
                                variable = new VariableRefNode(declaration.startToken, declaration.variable)
                            };
                            Compile(fadeStrAssignment);
                            break;
                        default:
                            // if the variable is a primitive, then always assign it to a default value.
                            var fakeAssignment = new AssignmentStatement
                            {
                                expression = new LiteralIntExpression(declaration.startToken, 0),
                                variable = new VariableRefNode(declaration.startToken, declaration.variable)
                            };
                            Compile(fakeAssignment);
                            break;
                    }
                    
                }
            }
        }

        public void PushAddress(ArrayIndexReference arrayRefNode)
        {
            if (!scope.TryGetArray(arrayRefNode.variableName, out var compiledArrayVar))
            {
                throw new Exception("Compiler: cannot access array since it not declared" +
                                    arrayRefNode.variableName);
            }

            var sizeOfElement = compiledArrayVar.byteSize;

            for (var i = 0; i < arrayRefNode.rankExpressions.Count; i++)
            {
                // load the multiplier factor for the term
                PushLoad(_buffer, compiledArrayVar.rankIndexScalerRegisterAddresses[i], compiledArrayVar.isGlobal);
                var expr = arrayRefNode.rankExpressions[i];
                Compile(expr); // load the expression index
                
                // duplicate the actual number so it can be used later in the math
                _buffer.Add(OpCodes.DUPE);
                
                // load up the max size for this rank of the array, 
                PushLoad(_buffer, compiledArrayVar.rankSizeRegisterAddresses[i], compiledArrayVar.isGlobal);
                
                // this will pull off the max-rank, then the dupe'd index value
                _buffer.Add(OpCodes.BOUNDS_CHECK);
                
                _buffer.Add(OpCodes.MUL);

                if (i > 0)
                {
                    _buffer.Add(OpCodes.ADD);
                }
            }

            // get the size of the element onto the stack
            AddPushInt(_buffer, sizeOfElement);
            
            // multiply the size of the element, and the index, to get the offset into the memory
            _buffer.Add(OpCodes.MUL);

            // load the array's ptr onto the stack, this is for the math of the offset
            PushLoadPtr(_buffer, compiledArrayVar.registerAddress, compiledArrayVar.isGlobal);
            
            // add the offset to the original pointer to get the write location
            _buffer.Add(OpCodes.ADD);

        }

        static void PushStorePtr(List<byte> buffer, ulong regAddr, bool isGlobal)
        {
            buffer.Add(isGlobal ? OpCodes.STORE_PTR_GLOBAL : OpCodes.STORE_PTR);
            // buffer.Add(regAddr);
            AddPushULongNoTypeCode(buffer, regAddr);
        }
        static void PushLoadPtr(List<byte> buffer, ulong regAddr, bool isGlobal)
        {
            buffer.Add(isGlobal ? OpCodes.LOAD_PTR_GLOBAL : OpCodes.LOAD_PTR);
            // buffer.Add(regAddr);
            AddPushULongNoTypeCode(buffer, regAddr);
        }

        static void PushStore(List<byte> buffer, ulong registerAddress, bool isGlobal)
        {
            buffer.Add(isGlobal ? OpCodes.STORE_GLOBAL : OpCodes.STORE);
            AddPushULongNoTypeCode(buffer, registerAddress);

        }
        static void PushLoad(List<byte> buffer, ulong registerAddress, bool isGlobal)
        {
            buffer.Add(isGlobal ? OpCodes.LOAD_GLOBAL : OpCodes.LOAD);
            AddPushULongNoTypeCode(buffer, registerAddress);
        }

        void CompileStructData(CompiledVariable compiledVar, bool ignoreType=true)
        {
            if (!_types.TryGetValue(compiledVar.structType, out var structType))
            {
                throw new Exception("Referencing type that does not exist yet. In assignment." + compiledVar.name + " and " + compiledVar.structType);
            }
            
            if (ignoreType)
                _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap

            // push the size of the write operation- it is the size of the struct we happen to have!
            AddPushInt(_buffer, structType.byteSize);
                        
            // now, push the pointer where to write the data to- which, we know is the register address
            PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
            
            _buffer.Add(OpCodes.WRITE); // consume the ptr, then the length, then the data
        }

        void CastToInt()
        {
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(TypeCodes.INT);
        }

        void CompileCast(byte typeCode)
        {
            _buffer.Add(OpCodes.CAST);
            _buffer.Add(typeCode);
        }
        
        void CompileAssignmentLeftHandSide(IVariableNode variable)
        {
            switch (variable)
            {
                case ArrayIndexReference arrayRefNode:
                    if (!scope.TryGetArray(arrayRefNode.variableName, out var compiledArrayVar))
                    {
                        throw new Exception("Compiler: cannot access array since it not declared" +
                                            arrayRefNode.variableName);
                    }
                    // always cast the expression to the correct type code; slightly wasteful, could be better.
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(compiledArrayVar.typeCode);
                    _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap
                    
                    var sizeOfElement = compiledArrayVar.byteSize;
                    AddPushInt(_buffer, sizeOfElement);

                    PushAddress(arrayRefNode);
                    // write! It'll find the ptr, then the size, and then the data itself
                    _buffer.Add(OpCodes.WRITE);
                    
                    break;
                case VariableRefNode variableRefNode:


                    if (scope.TryGetArray(variableRefNode.variableName, out var compiledArrayVariable))
                    {
                        // hopefully the rhs compilation pushed a ptr onto the stack, 
                        //  so the job here is to allocate some new memory, copy the memory, and then write the 
                        //  pointer to the register address for the array
                        
                        _buffer.Add(OpCodes.COPY_HEAP_MEM);
                        
                        // save the resulting pointer
                        PushStorePtr(_buffer, compiledArrayVariable.registerAddress, compiledArrayVariable.isGlobal);
                        
                        // // push the size of the write operation- it is the size of the struct we happen to have!
                        // // AddPushInt(_buffer, structType.byteSize);
                        // // AddPushInt(_buffer, 10);
                        // AddPushInt(_buffer, compiledArrayVariable.byteSize * 5); // the 5 is hardcoded for a test
                        //
                        // // now, push the pointer where to write the data to- which, we know is the register address
                        // // PushLoad(_buffer, comp.registerAddress, compiledVar.isGlobal);
                        // PushAddress(new ArrayIndexReference
                        // {
                        //     variableName = variableRefNode.variableName,
                        //     rankExpressions = new List<IExpressionNode>
                        //     {
                        //         new LiteralIntExpression(Token.Blank, 0)
                        //     }
                        // });
                        //
                        // _buffer.Add(OpCodes.WRITE); // consume the ptr, then the length, then the data
                        //
                        //
                        // _buffer.Add(OpCodes.CAST);
                        // _buffer.Add(compiledArrayVariable.typeCode);
                        //
                        // PushStorePtr(_buffer, compiledArrayVariable.registerAddress, compiledArrayVariable.isGlobal);

                        break;
                    }
                    
                    if (!scope.TryGetVariable(variableRefNode.variableName, out var compiledVar))
                    {
                        var tc = VmUtil.GetTypeCode(variableRefNode.DefaultTypeByName);
                        compiledVar = scope.Create(variableRefNode.variableName, tc, false);
                    }
                    
                    // wait wait, if the rhs is a pointer, and the lhs is a struct, then we actually need to COPY the pointer data...
                    if (compiledVar.typeCode == TypeCodes.STRUCT)
                    {
                        CompileStructData(compiledVar);
                        /*
                         * when this is getting set to an array- the entire struct data is sitting on the stack. The array-expression reads  it from the heap
                         * If we just just cast to struct, we'll just be capturing some random part of the memory...
                         * instead, we need to assume that the stack contains the right length amount of valid bytes to write into memory...
                         *
                         * we know the struct data here, or rather, we can...
                         */
                        // if (!_types.TryGetValue(compiledVar.structType, out var structType))
                        // {
                        //     throw new Exception("Referencing type that does not exist yet. In assignment." + compiledVar.name + " and " + compiledVar.structType);
                        // }
                        //
                        // _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap
                        //
                        // // push the size of the write operation- it is the size of the struct we happen to have!
                        // AddPushInt(_buffer, structType.byteSize);
                        //
                        // // now, push the pointer where to write the data to- which, we know is the register address
                        // PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                        //
                        // // the address is a struct ref, not an int, so for the write-command to work, we need to cast the struct to an int
                        // _buffer.Add(OpCodes.CAST);
                        // _buffer.Add(TypeCodes.INT);
                        //
                        // _buffer.Add(OpCodes.WRITE); // consume the ptr, then the length, then the data
                        break;
                    }
                    
                    // always cast the expression to the correct type code; slightly wasteful, could be better.
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(compiledVar.typeCode);
    
                    // store the value of the expression&cast in the desired register.
                    // _buffer.Add(OpCodes.STORE);
                    // _buffer.Add(compiledVar.registerAddress);
                    if (compiledVar.typeCode == TypeCodes.STRING)
                    {
                        PushStorePtr(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                    }
                    else
                    {
                        PushStore(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                    }
                    _dbg?.AddVariable(_buffer.Count - 1, compiledVar);
                    break;
                case StructFieldReference fieldReferenceNode:

                    switch (fieldReferenceNode.left)
                    {
                        case ArrayIndexReference arrayRefNode:
                            
                            // we need to find the start index of the array element,
                            // and then add the offset for the field access part (the right side)
                            if (!scope.TryGetArray(arrayRefNode.variableName, out var compiledLeftArrayVar))
                            {
                                throw new Exception("Compiler: cannot access array since it not declared" +
                                                    arrayRefNode.variableName);
                            }
                            
                            

                            var rightType = compiledLeftArrayVar.structType;
                            ComputeStructOffsets(rightType, fieldReferenceNode.right, out var rightOffset, out var rightLength, out var rightTypeCode);

                            // cast the value to the right type
                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(rightTypeCode);
                            
                            _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap

                            // load the write-length
                            AddPushInt(_buffer, rightLength);
                            
                            // load the offset of the right side
                            AddPushInt(_buffer, rightOffset);
                            
                            // load the array pointer
                            PushAddress(arrayRefNode);
                            
                            // add the pointer and the offset together
                            _buffer.Add(OpCodes.ADD);
                            
                            // write the data at the array index, by the offset, 
                            _buffer.Add(OpCodes.WRITE);
                            break;
                        
                        case VariableRefNode variableRef:
                            if (!scope.TryGetVariable(variableRef.variableName, out compiledVar))
                            {
                                FakeDeclare(variableRef, out compiledVar);
                            }

                            if (compiledVar.typeCode == TypeCodes.STRUCT)
                            {
                                
                            }
                            
                       
                            // load up the compiled type info 
                            var type = _types[compiledVar.structType];
                            ComputeStructOffsets(type, fieldReferenceNode.right, out var offset, out var length, out rightTypeCode);

                            // always cast the expression to the correct type code; slightly wasteful, could be better.
                            _buffer.Add(OpCodes.CAST);
                            _buffer.Add(rightTypeCode);
                            
                            _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap
                            
                            // push the length of the write segment
                            AddPushInt(_buffer, length);
                            
                            // load the base address of the variable
                            // _buffer.Add(OpCodes.LOAD);
                            // _buffer.Add(compiledVar.registerAddress);
                            PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                            
                            // load the offset of the right side
                            // AddPushInt(_buffer, offset);
                            AddPushPtr(_buffer, new VmPtr(){memoryPtr = offset}, TypeCodes.PTR_HEAP);
                            
                            // sum them, then the result is the ptr on the stack
                            _buffer.Add(OpCodes.ADD);
                            
                            // pull the ptr, length, then data, and return the ptr.
                            _buffer.Add(OpCodes.WRITE);
                            
                            break;
                        default:
                            throw new NotImplementedException("unhandled left side of operation");
                    }
                    break;
                default:
                    throw new NotImplementedException("Unsupported reference assignment");
            }
        }
        
        
        public void Compile(AssignmentStatement assignmentStatement)
        {
            /*
             * in order to assign, we need to know what we are assigning two, and find the correct place to put the result.
             *
             * If it is a simple variable, then it lives on a local register.
             * If it is an array, then it lives in memory.
             */

            // Note: assignment to a ref param inside a mock body is NOT
            // special-cased here — the body's value register is a regular
            // local. The writeback to the caller's variable happens at
            // every body exit (exitmock + endmock fall-through) via the
            // ref-binding list.

            if (assignmentStatement.variable is VariableRefNode leftRef &&
                scope.TryGetVariable(leftRef.variableName, out var leftVar) && leftVar.typeCode == TypeCodes.STRUCT)
            {
                // _buffer.Add(OpCodes.BREAKPOINT);
            }

            // compile the rhs of the assignment...
            Compile(assignmentStatement.expression);
            CompileAssignmentLeftHandSide(assignmentStatement.variable);

        }

        public void ComputeStructOffsets(CompiledType baseType, IVariableNode right, out int offset, out int writeLength, out byte typeCode)
        {
            writeLength = 0;
            offset = 0;

            switch (right)
            {
                case VariableRefNode variableRefNode:
                    var name = variableRefNode.variableName;
                    if (!baseType.fields.TryGetValue(name, out var member))
                    {
                        throw new Exception("Compiler: unknown member access " + name);
                    }

                    writeLength = member.Length;
                    offset += member.Offset;
                    typeCode = member.TypeCode;
                    break;
                case StructFieldReference structRef:

                    switch (structRef.left)
                    {
                        case VariableRefNode leftVariableRefNode:
                            var leftName = leftVariableRefNode.variableName;
                            if (!baseType.fields.TryGetValue(leftName, out var leftMember))
                            {
                                throw new Exception("Compiler: unknown member access " + leftName);
                            }
                            
                            ComputeStructOffsets(leftMember.Type, structRef.right, out offset, out writeLength, out typeCode);
                            offset += leftMember.Offset;
                            
                            break;
                        default:
                            throw new NotImplementedException("Cannot compute offsets for left");
                    }
                    // look up the field member of the left side, and then recursively call this function on the right.
                    // if (!baseType.fields.TryGetValue(name, out var leftMember))
                    // {
                    //     throw new Exception("Compiler: unknown member access " + name);
                    // }
                    
                    break;
                default:
                    throw new NotImplementedException("Cannot compute offsets");
            }
        }


        public void Compile(ExpressionStatement statement)
        {
            Compile(statement.expression);
            
            
            // nothing happens with the expression, because it isn't being assigned to anything...
            // but we don't know how big the result of the previous expression was... 
            _buffer.Add(OpCodes.DISCARD_TYPED);
            
            //
            
        }
        
        public void Compile(IExpressionNode expr)
        {

            // CompiledVariable compiledVar = null;
            switch (expr)
            {
                case DefaultValueExpression defExpr:
                    // the default value for any type is just zeros, right?

                    switch (defExpr.ParsedType.type)
                    {
                        case VariableType.String:
                            Compile(new LiteralStringExpression(defExpr.startToken, ""));
                            break;
                        case VariableType.Struct:
                            
                            if (_types.TryGetValue(defExpr.ParsedType.structName, out var typeInfo))
                            {
                                AddPushZeros(_buffer, TypeCodes.STRUCT, typeInfo.byteSize);
                            }
                            else
                            {
                                throw new Exception("unknown type reference" + defExpr.ParsedType.structName);
                            }
                            break;
                        default:
                            // push the type-code for this def-expr
                            var tc = VmUtil.GetTypeCode(defExpr.ParsedType.type);

                            // everything else is an empty zero block
                            AddPushZeros(_buffer, tc, TypeCodes.GetByteSize(tc));
                            break;
                    }
                    break;
                case CommandExpression commandExpr:
                    Compile(new CommandStatement
                    {
                        args = commandExpr.args,
                        command = commandExpr.command,
                        startToken = commandExpr.startToken,
                        endToken = commandExpr.endToken,
                        argMap = commandExpr.argMap
                    });
                    break;
                case LenExpression lenExpr:
                {
                    if (lenExpr.inner == null) { AddPushInt(_buffer, 0); break; }

                    // Array path: `len` is a structural query answered from
                    // the rank-size registers the compiler maintains for
                    // every array — never from the allocation's byte size
                    // (which is wrong for struct elements and flattens
                    // multi-dim arrays).
                    if (lenExpr.inner is VariableRefNode lenVarRef
                        && scope.TryGetArray(lenVarRef.variableName, out var lenArrayVar))
                    {
                        var rankCount = lenArrayVar.rankSizeRegisterAddresses.Length;
                        if (lenExpr.dimension == null || lenExpr.dimension is LiteralIntExpression)
                        {
                            // constant dimension — read the rank register directly.
                            // dimensions are zero-indexed, like array indexing.
                            var d = lenExpr.dimension is LiteralIntExpression litDim ? litDim.value : 0;
                            if (d < 0 || d >= rankCount)
                            {
                                // the visitor reports this as a parse error; guard
                                // here for compile-without-check callers.
                                throw new Exception($"Compiler: len dimension [{d}] out of range for array with [{rankCount}] dimensions");
                            }
                            PushLoad(_buffer, lenArrayVar.rankSizeRegisterAddresses[d], lenArrayVar.isGlobal);
                            break;
                        }

                        // Runtime dimension: spill the index to a scratch
                        // register so the expression evaluates exactly once,
                        // bounds-check it in [0, rank) (out of range is a fatal
                        // VM error, like an out-of-bounds array index), then
                        // select the rank register branchlessly:
                        // sum of (d == i) * size_i.
                        Compile(lenExpr.dimension);
                        CompileCast(TypeCodes.INT);
                        var lenDimReg = scope.AllocateRegister();
                        PushStore(_buffer, lenDimReg, isGlobal: false);

                        // BOUNDS_CHECK pops ceiling then index
                        PushLoad(_buffer, lenDimReg, isGlobal: false);
                        AddPushInt(_buffer, rankCount);
                        _buffer.Add(OpCodes.BOUNDS_CHECK);

                        for (var i = 0; i < rankCount; i++)
                        {
                            PushLoad(_buffer, lenDimReg, isGlobal: false);
                            AddPushInt(_buffer, i);
                            _buffer.Add(OpCodes.EQ);
                            PushLoad(_buffer, lenArrayVar.rankSizeRegisterAddresses[i], lenArrayVar.isGlobal);
                            _buffer.Add(OpCodes.MUL);
                            if (i > 0)
                            {
                                _buffer.Add(OpCodes.ADD);
                            }
                        }
                        break;
                    }

                    // String path: push the string heap pointer, then LENGTH
                    // divides the allocation byte size by the char size.
                    // Fade chars are uint codepoints — 4 bytes each.
                    Compile(lenExpr.inner);
                    _buffer.Add(OpCodes.LENGTH);
                    _buffer.Add(TypeCodes.GetByteSize(TypeCodes.INT));
                    break;
                }
                case DimsExpression dimsExpr:
                {
                    // `dims(arr)` — the highest valid dimension index, i.e.
                    // rank - 1, matching the zero-indexed `len(arr, k)` form:
                    // `for d = 0 to dims(arr)` iterates every dimension. The
                    // rank is compile-time knowledge, so this folds to a
                    // constant.
                    if (dimsExpr.inner is VariableRefNode dimsVarRef
                        && scope.TryGetArray(dimsVarRef.variableName, out var dimsArrayVar))
                    {
                        AddPushInt(_buffer, dimsArrayVar.rankSizeRegisterAddresses.Length - 1);
                        break;
                    }
                    // the visitor reports non-array args as parse errors;
                    // push 0 so compile-without-check callers stay balanced.
                    AddPushInt(_buffer, 0);
                    break;
                }
                case BytesExpression bytesExpr:
                {
                    // `bytes(x)` — memory size in bytes.
                    //  - type name / struct variable / scalar variable: constant
                    //  - array / string variable: runtime allocation size via
                    //    LENGTH with element size 1 (LENGTH *is* a runtime
                    //    sizeof; the 1 makes it report raw bytes).
                    if (bytesExpr.resolvedTypeName != null)
                    {
                        if (!_types.TryGetValue(bytesExpr.resolvedTypeName, out var bytesType))
                        {
                            throw new Exception("Compiler: bytes() references unknown type " + bytesExpr.resolvedTypeName);
                        }
                        AddPushInt(_buffer, bytesType.byteSize);
                        break;
                    }

                    if (bytesExpr.inner is VariableRefNode bytesVarRef)
                    {
                        if (scope.TryGetArray(bytesVarRef.variableName, out var bytesArrayVar))
                        {
                            PushLoadPtr(_buffer, bytesArrayVar.registerAddress, bytesArrayVar.isGlobal);
                            _buffer.Add(OpCodes.LENGTH);
                            _buffer.Add((byte)1);
                            break;
                        }

                        if (scope.TryGetVariable(bytesVarRef.variableName, out var bytesVar))
                        {
                            if (bytesVar.typeCode == TypeCodes.STRUCT)
                            {
                                if (!_types.TryGetValue(bytesVar.structType, out var bytesStructType))
                                {
                                    throw new Exception("Compiler: bytes() variable references unknown type " + bytesVar.structType);
                                }
                                AddPushInt(_buffer, bytesStructType.byteSize);
                                break;
                            }

                            if (bytesVar.typeCode == TypeCodes.STRING)
                            {
                                Compile(bytesVarRef);
                                _buffer.Add(OpCodes.LENGTH);
                                _buffer.Add((byte)1);
                                break;
                            }

                            AddPushInt(_buffer, TypeCodes.GetByteSize(bytesVar.typeCode));
                            break;
                        }

                        // compile-without-check fallback: the identifier may
                        // be a type name the visitor never resolved.
                        if (_types.TryGetValue(bytesVarRef.variableName, out var bytesFallbackType))
                        {
                            AddPushInt(_buffer, bytesFallbackType.byteSize);
                            break;
                        }

                        throw new Exception("Compiler: bytes() argument is neither a variable nor a type, " + bytesVarRef.variableName);
                    }

                    // General expression argument, e.g. `bytes(someCall())`.
                    // The expression is evaluated (it may have side effects)
                    // and its value discarded; the size comes from the
                    // expression's parsed type.
                    {
                        var bytesInnerType = bytesExpr.inner?.ParsedType ?? TypeInfo.Unset;
                        if (bytesExpr.inner == null)
                        {
                            AddPushInt(_buffer, 0);
                            break;
                        }

                        if (bytesInnerType.type == VariableType.String)
                        {
                            // string-valued expression: the ptr lands on the
                            // stack; LENGTH(1) reads the allocation byte size.
                            Compile(bytesExpr.inner);
                            _buffer.Add(OpCodes.LENGTH);
                            _buffer.Add((byte)1);
                            break;
                        }

                        if (bytesInnerType.type == VariableType.Struct
                            && bytesInnerType.structName != null
                            && _types.TryGetValue(bytesInnerType.structName, out var bytesExprStructType))
                        {
                            // struct-valued expression: the full struct data
                            // plus a typecode byte is on the stack — pop all
                            // of it, then push the compile-time size.
                            Compile(bytesExpr.inner);
                            for (var di = 0; di < bytesExprStructType.byteSize + 1; di++)
                            {
                                _buffer.Add(OpCodes.DISCARD);
                            }
                            AddPushInt(_buffer, bytesExprStructType.byteSize);
                            break;
                        }

                        // scalar expression: evaluate, discard the typed value,
                        // push the type's size.
                        Compile(bytesExpr.inner);
                        _buffer.Add(OpCodes.DISCARD_TYPED);
                        var bytesScalarTc = VmUtil.GetTypeCode(bytesInnerType.type);
                        AddPushInt(_buffer, TypeCodes.GetByteSize(bytesScalarTc));
                        break;
                    }
                }
                case CallCountExpression callCountExpr:
                {
                    // Resolve the command name to all overload ids; the count
                    // is per-id, but `call count <name>` means "across all
                    // overloads of <name>." Sum the per-id counts at runtime
                    // by emitting CALL_COUNT for each id and adding the
                    // results. For the common single-overload case this is
                    // just one CALL_COUNT instruction. If the name doesn't
                    // resolve to any command, push 0.
                    var ids = callCountExpr.commandName != null
                        ? ResolveMockCommandIds(callCountExpr.commandName)
                        : new List<int>();
                    if (ids.Count == 0)
                    {
                        AddPushInt(_buffer, 0);
                    }
                    else
                    {
                        EmitCallCountInline(ids[0]);
                        for (var i = 1; i < ids.Count; i++)
                        {
                            EmitCallCountInline(ids[i]);
                            _buffer.Add(OpCodes.ADD);
                        }
                    }
                    break;
                }
                case LiteralStringExpression literalString:
                    // allocate some memory for a string...
                    var str = literalString.value;
                    var strSize = str.Length * TypeCodes.GetByteSize(TypeCodes.INT);

                    if (_options.InternStrings)
                    {
                        
                        if (!stringToCallingInstructionIndexes.TryGetValue(str, out var indexes))
                        {
                            // capture this string as something that we need to intern... 
                            stringToCallingInstructionIndexes[str] = indexes = new HashSet<int>();
                        }

                        // take note that this index needs to be mapped back to the original string.
                        indexes.Add(_buffer.Count);
                    
                        // push a fake pointer onto the stack... The value gets replaced 
                        //  at RUNTIME as the machine is allocating the interned strings. 
                        // AddPushUInt(_buffer, int.MaxValue, includeTypeCode: false);
                        // AddPushInt(_buffer, int.MaxValue);
                        AddPushPtr(_buffer, VmPtr.TEMP, TypeCodes.PTR_HEAP);
                    }
                    else
                    {
                        // push the string data...
                        for (var i = 0 ; i < str.Length; i ++)
                        {
                            // push the string into the interned data, and then remember the pointer to that. 
                            var c = (uint)str[i];
                            AddPushUInt(_buffer, c, includeTypeCode:false);
                        }
                        
                        AddPushInt(_buffer, strSize); // SIZE, <Data>
                        
                        // this one will get used by the Write call
                        _buffer.Add(OpCodes.DUPE); // SIZE, SIZE, <Data>
                        
                        // add in the type-format
                        AddPushTypeFormat(_buffer, ref HeapTypeFormat.STRING_FORMAT);
                        
                        // allocate a ptr to the stack
                        _buffer.Add(OpCodes.ALLOC); // PTR, SIZE, <Data>
                        
                        _buffer.Add(OpCodes.WRITE_PTR); // consume the ptr, then the length, then the data
                    }
                    
                    
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(TypeCodes.STRING);
                    break;
                case LiteralRealExpression literalReal:
                    _buffer.Add(OpCodes.PUSH);
                    _buffer.Add(TypeCodes.REAL);
                    AppendFloat(_buffer, literalReal.value);
                    break;
                case LiteralIntExpression literalInt:
                    // push the literal value
                    AddPushInt(_buffer, literalInt.value);
                    break;
                case ArrayIndexReference arrayRef:
                    // need to fetch the value from the array...

                    if (!scope.TryGetArray(arrayRef.variableName, out var arrayVar))
                    {
                        CompileAsInvocation(arrayRef);
                        break;
                        // if (_functionTable.TryGetValue(arrayRef.variableName, out var func))
                        // {
                        //     break;
                        // }
                        // throw new Exception("compiler exception! the referenced array has not been declared yet " +
                        //                     arrayRef.variableName);
                    }
                    
                    var sizeOfElement = arrayVar.byteSize;

                    
                    // load the size up
                    AddPushInt(_buffer, sizeOfElement);

                    PushAddress(arrayRef);

                    // read, it'll find the ptr, size, and then place the data onto the stack
                    _buffer.Add(OpCodes.READ);
                    
                    // we need to inject the type-code back into the stack, since it doesn't exist in heap
                    _buffer.Add(OpCodes.BPUSH);
                    _buffer.Add(arrayVar.typeCode);

                    break;
                case StructFieldReference structRef:
                    // we need to load up the pointer, and read from the address...
                    switch (structRef.left)
                    {
                        case VariableRefNode variableRef:

                            if (!scope.TryGetVariable(variableRef.variableName, out var typeCompiledVar))
                            {
                                FakeDeclare(variableRef, out typeCompiledVar);
                            }

                            if (!_types.TryGetValue(typeCompiledVar.structType, out var type))
                            {
                                throw new Exception("Unknown type reference " + type);
                            }

                            ComputeStructOffsets(type, structRef.right, out var readOffset, out var readLength, out var readTypeCode);
                            
                            // push the size of the read operation
                            AddPushInt(_buffer, readLength);
                            
                            // push the read offset, so that we can add it to the ptr
                            // AddPushInt(_buffer, readOffset);
                            AddPushPtr(_buffer, new VmPtr{memoryPtr = readOffset}, TypeCodes.PTR_HEAP);
                            
                            // push the ptr of the variable, and cast it to an int for easy math
                            PushLoad(_buffer, typeCompiledVar.registerAddress, typeCompiledVar.isGlobal);
                            
                            // TODO: I removed these as part of the PTR refactor?
                            // _buffer.Add(OpCodes.CAST);
                            // _buffer.Add(TypeCodes.INT);
                            
                            // add those two op codes back together...
                            _buffer.Add(OpCodes.ADD);
                            
                            // read the summed ptr, then the length
                            _buffer.Add(OpCodes.READ);
                            
                            // we need to inject the type-code back into the stack, since it doesn't exist in heap
                            _buffer.Add(OpCodes.BPUSH);
                            _buffer.Add(readTypeCode);
                            
                            break;
                        case ArrayIndexReference arrayRefNode:
                            if (!scope.TryGetArray(arrayRefNode.variableName, out var leftArrayVar))
                            {
                                throw new CompilerException("compiler exception (3)! the referenced array has not been declared yet " +
                                                    arrayRefNode.variableName, arrayRefNode);
                            }
                            
                            var rightType = leftArrayVar.structType;
                            ComputeStructOffsets(rightType, structRef.right, out var rightOffset, out var rightLength, out var rightTypeCode);

                            // load the write-length
                            AddPushInt(_buffer, rightLength);
                            
                            // load the offset of the right side
                            AddPushInt(_buffer, rightOffset);
                            
                            // load the array pointer
                            PushAddress(arrayRefNode);
                            
                            // add the pointer and the offset together
                            _buffer.Add(OpCodes.ADD);
                            
                            // write the data at the array index, by the offset, 
                            _buffer.Add(OpCodes.READ);
                            _buffer.Add(OpCodes.BPUSH);
                            _buffer.Add(rightTypeCode);
                            break;
                        default:
                            throw new NotImplementedException("Cannot eval left based nested struct pointer");
                    }
                    break;
                case VariableRefNode variableRef:
                    
                    // maybe this is an array?
                    if (scope.TryGetArray(variableRef.variableName, out var compiledArrayVar))
                    {
                        // compile the pointer to this array?
                        PushLoadPtr(_buffer, compiledArrayVar.registerAddress, compiledArrayVar.isGlobal);
                        
                        // // ah, the entire memory needs to get pushed 
                        // // load the size up
                        // AddPushInt(_buffer, compiledArrayVar.byteSize * 5); // the 5 is hardcoded for a test
                        //
                        // PushAddress(new ArrayIndexReference
                        // {
                        //     variableName = variableRef.variableName,
                        //     rankExpressions = new List<IExpressionNode>
                        //     {
                        //         new LiteralIntExpression(Token.Blank, 0)
                        //     }
                        // });
                        // // PushLoad(_buffer, compiledArrayVar.registerAddress, compiledArrayVar.isGlobal);
                        //
                        // // read, it'll find the ptr, size, and then place the data onto the stack
                        // _buffer.Add(OpCodes.READ);
                        //
                        // // inject a type-code onto the stack
                        // _buffer.Add(OpCodes.BPUSH);
                        // _buffer.Add(compiledArrayVar.typeCode);

                        break;
                    }
                    
                    
                    // emit the read from register
                    if (!scope.TryGetVariable(variableRef.variableName, out var compiledVar))
                    {
                        // can we auto declare it?
                        FakeDeclare(variableRef, out compiledVar);
                    }

                    if (compiledVar.typeCode == TypeCodes.STRUCT)
                    {
                        // ah, if this is a struct, then we should push the entire contents of the memory pointer on the stack.
                        // CompileStructData(compiledVar, false);
                        // we don't want to WRITE- we need to READ it from memory
                        if (!_types.TryGetValue(compiledVar.structType, out var structType))
                        {
                            throw new Exception("Referencing type that does not exist yet. In value." + compiledVar.name + " and " + compiledVar.structType);
                        }
                        // load the size up
                        AddPushInt(_buffer, structType.byteSize);

                        PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                        
                        // read, it'll find the ptr, size, and then place the data onto the stack
                        _buffer.Add(OpCodes.READ);
                    
                        // we need to inject the type-code back into the stack, since it doesn't exist in heap
                        _buffer.Add(OpCodes.BPUSH);
                        _buffer.Add(TypeCodes.STRUCT);
                        break;
                    }
                    
                    PushLoad(_buffer, compiledVar.registerAddress, compiledVar.isGlobal);
                    break;
                case UnaryOperationExpression unary:
                    Compile(unary.rhs);
                    switch (unary.operationType)
                    {
                        case UnaryOperationType.Not:
                            _buffer.Add(OpCodes.NOT);
                            break;
                        case UnaryOperationType.Negate:
                            AddPushInt(_buffer, -1);
                            _buffer.Add(OpCodes.MUL);
                            break;
                        case UnaryOperationType.BitwiseNot:
                            _buffer.Add(OpCodes.BITWISE_NOT);
                            break;
                        default:
                            throw new Exception("Compiler: unsupported unary operaton " + unary.operationType);
                    }

                    break;
                case BinaryOperandExpression op:
                    
                    // TODO: At this point, we could decide _which_ add/mul method to call given the type.
                    // if both lhs and rhs are ints, then we could emit an IADD, and don't need to emit the type codes ahead of time.
                    
                    
                    // if the op is a short-circuitable operation, then we may not jump over parts of the compilation pass. 
                    /*
                     * a OR b ---- b only executes if !a
                     * a AND b --- b only executes if a
                     *
                     */
                    
                    /*
                     * Do we need to cast either side? For example, if we had
                     * 3.2 * 4, then the whole expression should be cast to a float
                     */
                    
                    Compile(op.lhs);
                    int jumpIndex = -1;
                    
                    switch (op.operationType)
                    {
                        case OperationType.Or:
                            // we need to jump based on the value, but also need the value to return 
                            _buffer.Add(OpCodes.DUPE);
                            CastToInt();
                        
                            // then, put a fake value in for the end of the rhs success jump... We'll fix it later.
                            jumpIndex = _buffer.Count;
                            AddPushInt(_buffer, int.MaxValue);
                    
                            // then, do the jump-gt-zero (because an or is happy if the value on the left is truthy)
                            _buffer.Add(OpCodes.JUMP_GT_ZERO);
                            break;
                        case OperationType.And:
                            // we need to jump based on the value, but also need the value to return 
                            _buffer.Add(OpCodes.DUPE);
                            CastToInt();

                            // then, put a fake value in for the end of the rhs success jump... We'll fix it later.
                            jumpIndex = _buffer.Count;
                            AddPushInt(_buffer, int.MaxValue);
                            
                            // then, jump if the value is 0, because if the lhs is false, then there is no point in checking the rhs; the whole AND expression will be false
                            _buffer.Add(OpCodes.JUMP_ZERO);
                            break;
                    }

                    switch (op.operationType)
                    {
                        // in DarkBasic, the not operator required a RHS even though it was ignored. 
                        //  for parity's sake, I'll allow it and just never compile the RHS for
                        //  bitwise nots. 
                        case OperationType.Bitwise_Not:
                            break;
                        default:
                            Compile(op.rhs);
                            break;
                    }

                    switch (op.operationType)
                    {
                        case OperationType.Add:
                            _buffer.Add(OpCodes.ADD);
                            break;
                        case OperationType.Mult:
                            _buffer.Add(OpCodes.MUL);
                            break;
                        case OperationType.RaisePower:
                            _buffer.Add(OpCodes.POWER);
                            break;
                        case OperationType.Divide:
                            _buffer.Add(OpCodes.DIVIDE);
                            break;
                        case OperationType.Mod:
                            _buffer.Add(OpCodes.MOD);
                            break;
                        case OperationType.Subtract:
                            // negate the second value, and add.
                            AddPushInt(_buffer, -1);
                            _buffer.Add(OpCodes.MUL);
                            _buffer.Add(OpCodes.ADD);
                            break;
                        case OperationType.GreaterThan:
                            _buffer.Add(OpCodes.GT);
                            break;
                        case OperationType.LessThan:
                            _buffer.Add(OpCodes.LT);
                            break;
                        case OperationType.GreaterThanOrEqualTo:
                            _buffer.Add(OpCodes.GTE);
                            break;
                        case OperationType.LessThanOrEqualTo:
                            _buffer.Add(OpCodes.LTE);
                            break;
                        case OperationType.EqualTo:
                            _buffer.Add(OpCodes.EQ);
                            break;
                        case OperationType.NotEqualTo:
                            _buffer.Add(OpCodes.EQ);
                            _buffer.Add(OpCodes.NOT);
                            break;
                        case OperationType.Xor:
                            _buffer.Add(OpCodes.LOGICAL_2);
                            _buffer.Add(OpCodes.BITWISE_XOR);
                            AddPushInt(_buffer, 1);
                            _buffer.Add(OpCodes.GTE);
                            break;
                        case OperationType.And:
                            _buffer.Add(OpCodes.LOGICAL_2);
                            _buffer.Add(OpCodes.MUL);
                            // push '1' onto the stack
                            AddPushInt(_buffer, 1);
                            _buffer.Add(OpCodes.GTE);
                            break;
                        case OperationType.Or:
                            _buffer.Add(OpCodes.LOGICAL_2);
                            _buffer.Add(OpCodes.ADD);
                            // push '1' onto the stack
                            AddPushInt(_buffer, 1);
                            _buffer.Add(OpCodes.GTE);
                            break;
                        case OperationType.Bitwise_And:
                            _buffer.Add(OpCodes.BITWISE_AND);
                            break;
                        case OperationType.Bitwise_Or:
                            _buffer.Add(OpCodes.BITWISE_OR);
                            break;
                        case OperationType.Bitwise_Xor:
                            _buffer.Add(OpCodes.BITWISE_XOR);
                            break;
                        case OperationType.Bitwise_Not:
                            _buffer.Add(OpCodes.BITWISE_NOT);
                            break;
                        case OperationType.Bitwise_LeftShift:
                            _buffer.Add(OpCodes.BITWISE_LEFTSHIFT);
                            break;
                        case OperationType.Bitwise_RightShift:
                            _buffer.Add(OpCodes.BITWISE_RIGHTSHIFT);
                            break;
                        default:
                            throw new NotImplementedException("unknown compiled op code: " + op.operationType);
                    }
                    
                    var endJumpValue = _buffer.Count;
                    if (jumpIndex > 0)
                    {
                        // ignore the type code for the jump...
                        _buffer.Add(OpCodes.NOOP); 
                        PatchInt32(_buffer, jumpIndex + 2, endJumpValue);
                    }
                    
                    break;
                default:
                    throw new Exception("compiler: unknown expression");
            }
            

        }

        
        void FakeDeclare(VariableRefNode refNode, out CompiledVariable compiledVar)
        {
            var fakeDeclStatement = new DeclarationStatement
            {
                startToken = refNode.startToken,
                endToken = refNode.endToken,
                ranks = null,
                scopeType = DeclarationScopeType.Local,
                variableNode = refNode,
                type = new TypeReferenceNode(refNode.DefaultTypeByName, refNode.startToken)
            };
            Compile(fakeDeclStatement);
            if (!scope.TryGetVariable(refNode.variableName, out compiledVar))
            {
                throw new CompilerException("compiler exception (5)! the referenced variable has not been declared yet " +
                                            refNode.variableName, refNode);
            }
        }

        private static void AddPush(List<byte> buffer, byte[] value, byte typeCode)
        {
            buffer.Add(OpCodes.PUSH);
            buffer.Add(typeCode);
            for (var i = 0 ; i < value.Length; i ++)
            // for (var i = value.Length - 1; i >= 0; i--)
            {
                buffer.Add(value[i]);
            }
        }

        private static void AddPushTypeFormat(List<byte> buffer, ref HeapTypeFormat format)
        {
            buffer.Add(OpCodes.PUSH_TYPE_FORMAT);
            HeapTypeFormat.AddToBuffer(ref format, buffer);
        }

        private static void AddPushPtr(List<byte> buffer, VmPtr ptr, byte typeCode)
        {
            buffer.Add(OpCodes.PUSH);
            buffer.Add(typeCode);
            var value = VmPtr.GetBytes(ref ptr);
            for (var i = 0; i < value.Length; i++)
                // for (var i = value.Length - 1; i >= 0; i--)
            {
                buffer.Add(value[i]);
            }
        }
        
        private static void AddPushInt(List<byte> buffer, int x)
        {
            buffer.Add(OpCodes.PUSH);
            buffer.Add(TypeCodes.INT);
            AppendInt32(buffer, x);
        }

        private static void AddPushZeros(List<byte> buffer, byte typeCode, int howManyBytesOfZero)
        {
            buffer.Add(OpCodes.PUSH_ZEROS);
            buffer.Add(typeCode);
            AppendInt32(buffer, howManyBytesOfZero);
        }

        private static void AddPushULongNoTypeCode(List<byte> buffer, ulong x)
        {
            AppendUInt64(buffer, x);
        }

        private static void AddPushUInt(List<byte> buffer, uint x, bool includeTypeCode=true)
        {
            buffer.Add(includeTypeCode ? OpCodes.PUSH : OpCodes.PUSH_TYPELESS);
            buffer.Add(TypeCodes.INT);
            AppendInt32(buffer, (int)x);
        }

        private static void AppendInt32(List<byte> buffer, int value)
        {
            buffer.Add((byte)(value));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 24));
        }

        private static void AppendUInt64(List<byte> buffer, ulong value)
        {
            buffer.Add((byte)(value));
            buffer.Add((byte)(value >> 8));
            buffer.Add((byte)(value >> 16));
            buffer.Add((byte)(value >> 24));
            buffer.Add((byte)(value >> 32));
            buffer.Add((byte)(value >> 40));
            buffer.Add((byte)(value >> 48));
            buffer.Add((byte)(value >> 56));
        }

        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
        private struct FloatIntUnion
        {
            [System.Runtime.InteropServices.FieldOffset(0)] public float Float;
            [System.Runtime.InteropServices.FieldOffset(0)] public int Int;
        }

        private static void AppendFloat(List<byte> buffer, float value)
        {
            var u = new FloatIntUnion { Float = value };
            AppendInt32(buffer, u.Int);
        }

        private static void PatchInt32(List<byte> buffer, int index, int value)
        {
            buffer[index]     = (byte)(value);
            buffer[index + 1] = (byte)(value >> 8);
            buffer[index + 2] = (byte)(value >> 16);
            buffer[index + 3] = (byte)(value >> 24);
        }
    }
}
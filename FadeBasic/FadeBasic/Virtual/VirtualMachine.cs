using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using FadeBasic.Ast;
using FadeBasic.Json;

namespace FadeBasic.Virtual
{


    public class ExecutionState
    {
        public bool isComplete;
        
    }
    
    public struct VirtualScope
    {
        public const byte FLAG_GLOBAL = 1;
        public const byte FLAG_PTR = 2;

        public static bool IsGlobal(byte flags)
        {
            return (flags & FLAG_GLOBAL) > 0;
        }
        public static bool IsPtr(byte flags)
        {
            return (flags & FLAG_PTR) > 0;
        }
        
        // parallel arrays with dataReg
        public ulong[] dataRegisters;
        
        /// <summary>
        /// The type codes
        /// </summary>
        public byte[] typeRegisters;
        
        public int[] insIndexes;
        public byte[] flags;

        public FastStack<int> deferredJumps;
        
        public VirtualScope(ulong initialCapacity)
        {
            dataRegisters = new ulong[initialCapacity];
            typeRegisters = new byte[initialCapacity];
            insIndexes = new int[initialCapacity];
            flags = new byte[initialCapacity];
            deferredJumps = new FastStack<int>(4);
        }
    }

    public struct JumpHistoryData
    {
        public int fromIns;
        public int toIns;
        // True when this frame was pushed by a test launcher's GOSUB
        // (JUMP_HISTORY_LAUNCH). Launcher frames are control-flow plumbing
        // — they exist so a test's `from`-chain can run as a sequence of
        // GOSUBs — and shouldn't appear in user-facing stack traces. The
        // RETURN opcode still pops them; only stack-trace capture filters.
        public bool isLauncherFrame;
    }

    public struct VirtualRuntimeError
    {
        public VirtualRuntimeErrorType type;
        public int insIndex;
        public string message;
        // Snapshot of the VM's methodStack at the moment the error was raised
        // (innermost frame first). Resolution to source locations is done by
        // a downstream consumer that has access to DebugData. Empty when the
        // error happened with no function calls in flight.
        public JumpHistoryData[] callStack;
    }

    public enum VirtualRuntimeErrorType
    {
        NONE,
        DIVIDE_BY_ZERO,
        INVALID_POWER,
        INVALID_ADDRESS,
        INVALID_MEMORY_COPY,
        CANNOT_TOKENIZE_WITHOUT_HIGHER_CONTEXT,
        EXPLODE,
        ASSERT_FAILED
    }

    public class TokenReplacement
    {

        public int substitutionCount;
        public int tokenStartIndex;
        public int tokenEndIndex;

        // public int endPadding = 0; //2 for regular block
        // public int startPadding = 1;
        public int tokenBlockIndex;
        public List<TokenSubstitutionReplacement> substitutionReplacements = new List<TokenSubstitutionReplacement>();
    }

    public class TokenSubstitutionReplacement
    {
        public int tokenIndex;
        public int tokenStartIndex, tokenEndIndex;
        public byte typeCode;
        public bool isStringify;
        public TransitiveTypeFlags transitiveTypeFlags;
        public object raw;
    }
    
    public class VirtualMachine
    {
        public byte[] program; // TODO: this could be readonly, except for the REPL.

        public int instructionIndex;

        
        public FastStack<byte> stack = new FastStack<byte>(256);
        public VmHeap heap;

        /// <summary>
        /// How many heap allocations may happen between garbage collections.
        /// Collection triggers are checked at pointer stores, heap writes, and
        /// allocs; a collection only runs when at least this many allocations
        /// occurred since the last one. 1 collects at every opportunity — the
        /// most aggressive behavior, which the GC tests rely on. The default
        /// amortizes the O(live allocations) trace cost while keeping
        /// unreachable memory short-lived.
        /// </summary>
        public int sweepInterval = DEFAULT_SWEEP_INTERVAL;
        public const int DEFAULT_SWEEP_INTERVAL = 64;

        // scratch state for CollectGarbage, reused across collections
        private HashSet<VmPtr> _gcMarks;
        private Stack<VmPtr> _gcWork;
        private List<VmPtr> _internedPtrs;


        public HostMethodTable hostMethods;
        public FastStack<JumpHistoryData> methodStack; // TODO: This could also store the index of the scope-stack at the time of the push; so that a debugger could know the scope at the frame.

        public VirtualScope globalScope;
        public FastStack<VirtualScope> scopeStack;

        public VirtualScope scope;

        public IDebugLogger logger;
        public VirtualRuntimeError error = new VirtualRuntimeError();

        public ulong[] dataRegisters => scope.dataRegisters; // TODO: optimize to remove method call Peek()
        public byte[] typeRegisters => scope.typeRegisters;

        public int internedDataInstructionIndex;
        public bool shouldThrowRuntimeException;

        public List<TokenReplacement> tokenReplacements;

        /// <summary>
        /// Stack of pending runto frames. Each frame records the target program
        /// address the test asked to advance to, and the test-side instruction
        /// the VM should resume at when that target is hit.
        /// </summary>
        public FastStack<RuntoFrame> runtoStack = new FastStack<RuntoFrame>(4);

        /// <summary>
        /// Where the program should resume from on the next `runto`. On the very
        /// first `runto`, this points at the program's main entry (instructionIndex
        /// after interned-data setup, i.e. 4). On subsequent runtos, this is the
        /// saved IP from the most recent RUNTO_YIELD.
        /// </summary>
        public int programResumeIP;

        /// <summary>
        /// Set when an `assert` fails during test execution. Null means the test
        /// has not failed any assertions (yet). The test runner inspects this
        /// after Execute() returns to determine pass/fail.
        /// </summary>
        public TestFailure assertionFailure;

        /// <summary>
        /// True when this VM is running a test entry point. The test runner sets
        /// this before <c>Execute</c> so a failed `assert` (anywhere — even in
        /// main-program code reached via runto) records a TestFailure and halts
        /// instead of throwing a runtime exception. When false, a failed assert
        /// triggers a normal VM runtime error, identical to divide-by-zero etc.
        /// </summary>
        public bool isTestExecution;

        public class TestFailure
        {
            public string sourceText;   // Captured text of the asserted expression.
            public int instructionIndex; // IP at the moment of failure (for source-mapping).
            public string reason;       // Optional reason string from `assert <cond>, "<reason>"`. Empty when not provided.
            // Snapshot of methodStack at the moment of failure. Innermost frame
            // first (top of stack). Each entry's fromIns is the call site of
            // that frame; toIns is the function's entry address. Empty when the
            // assert fired at the test entry level with no function calls in
            // between. Used to build a source-mapped call stack for the failure
            // report; resolution happens in the test runner, not the VM.
            public JumpHistoryData[] callStack = System.Array.Empty<JumpHistoryData>();
        }

        /// <summary>
        /// Per-VM mock registrations. Keyed by host method id (the index into
        /// <see cref="HostMethodTable.methods"/>). On CALL_HOST the dispatcher
        /// consults this table first; if a registration exists, it pops the
        /// command's args via metadata and synthesizes the mock behavior in
        /// place of the real call.
        /// </summary>
        public Dictionary<int, MockBehavior> mockTable;

        /// <summary>
        /// Per-VM host-call counter. Incremented on every CALL_HOST (mocked or
        /// not) when <see cref="isTestExecution"/> is true. Read by the
        /// <c>call count &lt;command&gt;</c> expression so tests can assert
        /// how often a command was invoked. Keyed by host method id, same as
        /// <see cref="mockTable"/>. Null until the first increment.
        /// </summary>
        public Dictionary<int, int> hostCallCounts;

        public class MockBehavior
        {
            // 0 = void (skip), 1 = returns (push value), 2 = forbid (assert-fail),
            // 3 = body (run bytecode block — Phase B onward).
            public byte kind;
            // For kind = Returns: the typed return value to push.
            public byte returnTypeCode;
            public byte[] returnBytes;
            // For kind = Forbid: optional user-supplied reason text (empty
            // when the user wrote `forbid` with no reason) and the address
            // of the assert-unwind trampoline so a forbid failure can drain
            // defers the same way an assert failure does.
            public string forbidReason;
            public int forbidTrampolineAddr;
            // For kind = Body: bytecode address of the mock body. CALL_HOST
            // pushes methodStack and jumps here. The body itself pushes a
            // scope, binds args from the stack as locals, runs user code,
            // pops scope, and RETURNs to the caller.
            public int bodyAddr;
        }

        public VirtualMachine(IEnumerable<byte> program) : this(program.ToArray())
        {
        }
        public VirtualMachine(byte[] program) : this(program, 4)
        {
        }
        public VirtualMachine(byte[] program, int entryPointAddress)
        {
            this.program = program;
            shouldThrowRuntimeException = true;
            // scope = new VirtualScope(256);
            scopeStack = new FastStack<VirtualScope>(16);
            methodStack = new FastStack<JumpHistoryData>(16);
            heap = new VmHeap(128);

            instructionIndex = entryPointAddress;
            programResumeIP = 4;
            internedDataInstructionIndex = BitConverter.ToInt32(program, 0);


            ReadInternedData();
            globalScope = scope = new VirtualScope(internedData.maxRegisterAddress);
            scopeStack.Push(globalScope);

            // scopeStack.Push(scope);
        }


        /// <summary>
        /// Tracing garbage collection: compute the set of reachable heap
        /// allocations and free everything else.
        ///
        /// Roots:
        ///  - interned strings (pinned for the lifetime of the VM)
        ///  - every register in every live scope whose type code or flags say
        ///    it holds a heap pointer (over-approximated on purpose; a stale
        ///    flag can only retain garbage, never free a live object)
        ///  - the eval stack, scanned conservatively: any 8-byte window whose
        ///    bytes match a live allocation's pointer is treated as a
        ///    reference. This keeps mid-expression temporaries alive, e.g. a
        ///    concat result sitting on the stack while a called function runs.
        ///
        /// Reachability then flows through heap data: arrays of strings,
        /// struct string fields, and nested structs are traced via each
        /// allocation's HeapTypeFormat and the interned type table.
        /// </summary>
        public void CollectGarbage()
        {
            var marks = _gcMarks ?? (_gcMarks = new HashSet<VmPtr>());
            var work = _gcWork ?? (_gcWork = new Stack<VmPtr>());
            marks.Clear();
            work.Clear();

            if (_internedPtrs != null)
            {
                for (var i = 0; i < _internedPtrs.Count; i++)
                {
                    GcMark(_internedPtrs[i]);
                }
            }

            GcMarkScope(ref globalScope);
            GcMarkScope(ref scope);
            for (var i = 0; i < scopeStack.ptr; i++)
            {
                GcMarkScope(ref scopeStack.buffer[i]);
            }

            // conservative eval-stack scan: pointers are stored as
            // [bucket int][memory int], the same layout VmPtr.GetBytes writes.
            for (var i = 0; i + 8 <= stack.ptr; i++)
            {
                var candidate = new VmPtr
                {
                    bucketPtr = BitConverter.ToInt32(stack.buffer, i),
                    memoryPtr = BitConverter.ToInt32(stack.buffer, i + 4),
                };
                GcMark(candidate);
            }

            while (work.Count > 0)
            {
                GcTrace(work.Pop());
            }

            heap.SweepUnmarked(marks);
        }

        void GcMark(VmPtr ptr)
        {
            if (heap.IsAllocated(ptr) && _gcMarks.Add(ptr))
            {
                _gcWork.Push(ptr);
            }
        }

        void GcMarkScope(ref VirtualScope vScope)
        {
            if (vScope.dataRegisters == null) return;
            for (var r = 0; r < vScope.dataRegisters.Length; r++)
            {
                var tc = vScope.typeRegisters[r];
                if (tc == TypeCodes.STRING || tc == TypeCodes.STRUCT || tc == TypeCodes.PTR_HEAP
                    || VirtualScope.IsPtr(vScope.flags[r]))
                {
                    GcMark(VmPtr.FromRaw(vScope.dataRegisters[r]));
                }
            }
        }

        void GcTrace(VmPtr ptr)
        {
            if (!heap.TryGetAllocation(ptr, out var allocation)) return;
            var format = allocation.format;

            if (format.IsArray(out _))
            {
                switch (format.typeCode)
                {
                    case TypeCodes.STRING:
                    {
                        // string elements are 8-byte heap pointers
                        heap.ReadSpan(ptr, allocation.length, out var span);
                        for (var offset = 0; offset + 8 <= allocation.length; offset += 8)
                        {
                            GcMark(VmPtr.FromBytes(span.Slice(offset)));
                        }
                        break;
                    }
                    case TypeCodes.STRUCT:
                    {
                        // struct elements are stored inline, one type-sized slot each
                        if (!typeTable.TryGetValue(format.typeId, out var elementType) || elementType.byteSize <= 0)
                            break;
                        heap.ReadSpan(ptr, allocation.length, out var span);
                        for (var offset = 0; offset + elementType.byteSize <= allocation.length; offset += elementType.byteSize)
                        {
                            GcTraceStructFields(span, offset, elementType);
                        }
                        break;
                    }
                }
                return;
            }

            if (format.typeCode == TypeCodes.STRUCT && typeTable.TryGetValue(format.typeId, out var structType))
            {
                heap.ReadSpan(ptr, allocation.length, out var structSpan);
                GcTraceStructFields(structSpan, 0, structType);
            }
            // strings and scalar arrays are leaves
        }

        void GcTraceStructFields(ReadOnlySpan<byte> span, int baseOffset, InternedType type)
        {
            foreach (var kvp in type.fields)
            {
                var field = kvp.Value;
                switch (field.typeCode)
                {
                    case TypeCodes.STRING:
                        if (baseOffset + field.offset + 8 <= span.Length)
                        {
                            GcMark(VmPtr.FromBytes(span.Slice(baseOffset + field.offset)));
                        }
                        break;
                    case TypeCodes.STRUCT:
                        // nested structs are inline; recurse into their fields
                        if (typeTable.TryGetValue(field.typeId, out var fieldType))
                        {
                            GcTraceStructFields(span, baseOffset + field.offset, fieldType);
                        }
                        break;
                }
            }
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private byte Advance() => program[instructionIndex++];

        public IEnumerator<ExecutionState> Execute(int instructionBatchCount = 1000)
        {
            // TODO: clean this up...
            Execute2();
            yield return new ExecutionState
            {
                isComplete = true
            };
        }


        public bool isSuspendRequested;
        public void Suspend()
        {
            isSuspendRequested = true;
        }

        /// <summary>
        /// The state structure keeps track of data between instruction evaluations.
        /// If you were to run the <see cref="VirtualMachine.Execute2"/> with infinite budget, then
        /// these variables would be kept local within the for-loop.
        /// But if you ran the method 1000 times with a budget of 1, these variables need to be restored. 
        /// </summary>
        public struct VmState
        {
            public byte vTypeCode, typeCode, size;
            public ulong data, addr;
            public int insPtr;
        }

        public VmState state = new VmState();
        public InternedData internedData;
        public Dictionary<int, InternedType> typeTable = new Dictionary<int, InternedType>();
        
        void ReadInternedData()
        {
            
            var internedBytes =
                program.AsSpan(internedDataInstructionIndex, program.Length - internedDataInstructionIndex);
            
            
            /*
             * the byte[] represents a blob of data.
             * Ideally it would be a straight forward parse...
             *   JSON is easiest, but not as performant...
             *   a custom format is hardest, but would be fast. 
             */
            var json = Encoding.Default.GetString(internedBytes.ToArray());
            internedData = JsonableExtensions.FromJson<InternedData>(json);

            foreach (var kvp in internedData.types)
            {
                typeTable[kvp.Value.typeId] = kvp.Value;
            }

            foreach (var str in internedData.strings)
            {
                var size = str.value.Length * TypeCodes.GetByteSize(TypeCodes.INT);
                heap.AllocateString(size, out var ptr);

                // this interned string should never be free'd, because we don't know when it will need to be accessed.
                (_internedPtrs ?? (_internedPtrs = new List<VmPtr>())).Add(ptr);
                var span = new byte[size];
                for (var i = 0; i < str.value.Length; i++)
                {
                    var data = (uint)str.value[i];
                    var bytes = BitConverter.GetBytes(data);
                    span[i * 4 + 0] = bytes[0];
                    span[i * 4 + 1] = bytes[1];
                    span[i * 4 + 2] = bytes[2];
                    span[i * 4 + 3] = bytes[3];
                }

                heap.Write(ptr, size, span);

                var ptrBytes = VmPtr.GetBytes(ref ptr);
                foreach (var index in str.indexReferences)
                {
                    // replace the ptr starting at index with the actual assigned ptr
                    // start at 2 to handle type-code
                    program[index + 2] = ptrBytes[0];
                    program[index + 3] = ptrBytes[1];
                    program[index + 4] = ptrBytes[2];
                    program[index + 5] = ptrBytes[3];
                    
                    program[index + 6] = ptrBytes[4];
                    program[index + 7] = ptrBytes[5];
                    program[index + 8] = ptrBytes[6];
                    program[index + 9] = ptrBytes[7];
                }
            }
        }

        public void Execute2(int instructionBatchCount = 1000, Func<int, bool> shouldBreakpointCallback = null)
        {
            Execute3(instructionBatchCount, shouldBreakpointCallback);
        }
        
        public int Execute3(int instructionBatchCount=1000, Func<int, bool> shouldBreakpointCallback=null)
        {
            isSuspendRequested = false;
            var cycles = 0;
            
            // while (true)
            {

                // the arrays do not need to be held between instruction evaluations
                byte[] aBytes;
                byte[] bBytes;
                ReadOnlySpan<byte> aSpan, bSpan, cSpan;
                
                // these pointer/data values need to be held between instruction evaluations, and therefor stay in the state. 
                byte vTypeCode = state.vTypeCode, typeCode = state.typeCode;
                ulong data = state.data, addr = state.addr;
                byte size = state.size;
                int insPtr = state.insPtr;
                
                // var sw = new Stopwatch();
                var incrementer = instructionBatchCount > 0 ? 1 : 0;
                for (var i = 0; 
                     (instructionBatchCount == 0 || i < instructionBatchCount)
                        && instructionIndex < program.Length 
                        && !isSuspendRequested
                        && error.type == VirtualRuntimeErrorType.NONE; 
                     i += incrementer)
                {
                    cycles++;

                    // Runto max-cycles enforcement. Only the topmost frame ticks,
                    // so nested runtos each get their own independent budget and
                    // an outer frame's budget pauses while an inner runto is active.
                    if (runtoStack.Count > 0)
                    {
                        ref var runtoTop = ref runtoStack.buffer[runtoStack.ptr - 1];
                        if (--runtoTop.cyclesRemaining < 0)
                        {
                            assertionFailure = new TestFailure
                            {
                                sourceText = "RUNTO exceeded max cycles",
                                instructionIndex = instructionIndex
                            };
                            instructionIndex = int.MaxValue;
                            break;
                        }
                    }

                    var ins = Advance();
                    switch (ins)
                    {
                        case OpCodes.PUSH:
                            typeCode = Advance();
                            size = TypeCodes.GetByteSize(typeCode);
                            stack.PushArray(program, instructionIndex, size);
                            stack.Push(typeCode);
                            instructionIndex += size;
                            
                            break;
                        case OpCodes.CAST:
                            typeCode = Advance();
                            VmUtil.Cast(ref stack, typeCode);
                            break;

                        case OpCodes.PUSH_SCOPE:
                            var newScope = new VirtualScope(internedData.maxRegisterAddress);
                            scopeStack.buffer[scopeStack.ptr - 1].deferredJumps = scope.deferredJumps; // recommit scope memory.
                            scopeStack.Push(newScope);
                            scope = newScope;
                            break;
                        case OpCodes.POP_SCOPE:

                            // popping the scope is what makes its registers
                            // unreachable; the tracing collector reclaims
                            // anything they were the last reference to.
                            scopeStack.Pop();
                            scope = scopeStack.buffer[scopeStack.ptr - 1];
                            break;
                        case OpCodes.JUMP_TABLE:
                            VmUtil.ReadAsInt(ref stack, out var tableSize);
                            int[] addresses = new int[tableSize];
                            ulong[] values = new ulong[tableSize];
                            for (var j = 0; j < tableSize; j++)
                            {
                                VmUtil.ReadSpanAsUInt(ref stack, out var hash);
                                VmUtil.ReadAsInt(ref stack, out var caseAddr);
                                addresses[j] = caseAddr;
                                values[j] = hash;
                            }
                            
                            VmUtil.ReadAsInt(ref stack, out var defaultAddr);
                            VmUtil.ReadSpanAsUInt(ref stack, out var key);

                            var found = false;
                            for (var j = 0; j < tableSize; j++)
                            {
                                if (key == values[j])
                                {
                                    instructionIndex = addresses[j];
                                    found = true;
                                    break;
                                }
                            }

                            if (!found)
                            {
                                instructionIndex = defaultAddr;
                            }
                            
                            break;
                        case OpCodes.JUMP:
                            // the next instruction is the instruction ptr
                            VmUtil.ReadAsInt(ref stack, out insPtr);
                            instructionIndex = insPtr;
                            break;
                        case OpCodes.JUMP_GT_ZERO:
                            VmUtil.ReadAsInt(ref stack, out insPtr);
                            VmUtil.ReadAsInt(ref stack, out var jumpValue);
                            if (jumpValue > 0)
                            {
                                instructionIndex = insPtr;
                            }
                            break;
                        
                        case OpCodes.JUMP_ZERO:
                            VmUtil.ReadAsInt(ref stack, out insPtr);
                            VmUtil.ReadAsInt(ref stack, out var jumpValue3);
                            if (jumpValue3 == 0)
                            {
                                instructionIndex = insPtr;
                            }
                            break;
                        case OpCodes.POP_DEFER:
                            int jumpSite = 0;
                            if (scope.deferredJumps.Count > 0)
                            {
                                jumpSite = scope.deferredJumps.Pop();
                            }
                            stack.PushSpanAndType(new ReadOnlySpan<byte>(BitConverter.GetBytes(jumpSite)), TypeCodes.INT, TypeCodes.GetByteSize(TypeCodes.INT));
                            break;
                        case OpCodes.PUSH_SCOPE_DEPTH:
                            // Push current scope-stack depth as a typed int. Depth 1 = global only.
                            stack.PushSpanAndType(
                                new ReadOnlySpan<byte>(BitConverter.GetBytes(scopeStack.Count)),
                                TypeCodes.INT, TypeCodes.GetByteSize(TypeCodes.INT));
                            break;
                        case OpCodes.CALL_COUNT:
                        {
                            // Inline 4-byte command id; push that command's
                            // host-call count as a typed int. Unknown command
                            // ids (never invoked) push 0.
                            var cmdId = BitConverter.ToInt32(program, instructionIndex);
                            instructionIndex += 4;
                            var count = 0;
                            hostCallCounts?.TryGetValue(cmdId, out count);
                            stack.PushSpanAndType(
                                new ReadOnlySpan<byte>(BitConverter.GetBytes(count)),
                                TypeCodes.INT, TypeCodes.GetByteSize(TypeCodes.INT));
                            break;
                        }
                        case OpCodes.PUSH_DEFER:
                            // read the place we should jump to when the scope is popped. 
                            VmUtil.ReadAsInt(ref stack, out var a);

                            scope.deferredJumps.Push(a);
                            
                            break;
                        case OpCodes.JUMP_HISTORY:
                            // the next instruction is the instruction ptr
                            VmUtil.ReadAsInt(ref stack, out insPtr);
                            methodStack.Push(new JumpHistoryData
                            {
                                toIns = insPtr,
                                fromIns = instructionIndex
                            }) ;
                            logger?.Log($"[VM] JUMP HISTORY FROM=[{instructionIndex}] TO=[{insPtr}]");
                            instructionIndex = insPtr;
                            break;
                        case OpCodes.JUMP_HISTORY_LAUNCH:
                            // Identical to JUMP_HISTORY but tags the frame as
                            // launcher-pushed so CaptureCallStack filters it.
                            VmUtil.ReadAsInt(ref stack, out insPtr);
                            methodStack.Push(new JumpHistoryData
                            {
                                toIns = insPtr,
                                fromIns = instructionIndex,
                                isLauncherFrame = true
                            });
                            instructionIndex = insPtr;
                            break;
                        case OpCodes.RETURN:
                            if (methodStack.ptr > 0)
                            {
                                /*
                                 * the use case to allow a return on an empty stack is
                                 * using GOSUB and not adding an END statement before the program hits the labels.
                                 */
                                var jumpHistoryData = methodStack.Pop();
                                instructionIndex = jumpHistoryData.fromIns;
                            }
                            break;
                        case OpCodes.DUPE:
                            // look at the stack, and push stuff onto it...
                            VmUtil.ReadSpan(ref stack, out typeCode, out aSpan);
                            VmUtil.PushSpan(ref stack, aSpan, typeCode);
                            VmUtil.PushSpan(ref stack, aSpan, typeCode);
                            break;
                        case OpCodes.BPUSH:
                            var code = Advance();
                            stack.Push(code);
                            break;
                        case OpCodes.PUSH_ZEROS:
                            // next 4 bytes in INS are zero-amount
                            typeCode = Advance();
                            var amount = BitConverter.ToInt32(program, instructionIndex);
                            // var amountSpan = program.AsSpan(instructionIndex, 4);
                            instructionIndex += sizeof(int);
                            stack.PushFiller(0, amount);
                            stack.Push(typeCode);

                            break;

                        case OpCodes.PUSH_TYPELESS:
                            typeCode = Advance();
                            size = TypeCodes.GetByteSize(typeCode);
                            stack.PushArray(program, instructionIndex, size);
                            instructionIndex += size;
                            break;
                        
                        case OpCodes.PUSH_TYPE_FORMAT:
                            stack.PushArray(program, instructionIndex, HeapTypeFormat.SIZE);
                            instructionIndex += HeapTypeFormat.SIZE;
                            break;
                            
                        case OpCodes.NOT:
                            VmUtil.ReadSpan(ref stack, out typeCode, out aSpan);
                            VmUtil.Not(typeCode, aSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, typeCode);
                            break;
                        case OpCodes.MIN_MAX_PUSH:
                            // throw new NotImplementedException();

                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.GetMinMax(vTypeCode, aSpan, bSpan, out var needsFlip);
                            
                            aBytes = aSpan.ToArray();
                            bBytes = bSpan.ToArray();
                            if (needsFlip)
                            {
                                VmUtil.PushSpan(ref stack, aBytes, typeCode);
                                VmUtil.PushSpan(ref stack, bBytes, typeCode);
                            }
                            else
                            {
                                VmUtil.PushSpan(ref stack, bBytes, typeCode);
                                VmUtil.PushSpan(ref stack, aBytes, typeCode);
                            }
                            break;
                        case OpCodes.BITWISE_AND:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.BitwiseAnd(ref heap, vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.BITWISE_OR:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.BitwiseOr(ref heap, vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.BITWISE_XOR:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.BitwiseXor(ref heap, vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.BITWISE_LEFTSHIFT:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.BitwiseLeftShift(ref heap, vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.BITWISE_RIGHTSHIFT:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.BitwiseRightShift(ref heap, vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.BITWISE_NOT:
                            VmUtil.ReadSpan(ref stack, out typeCode, out aSpan);
                            VmUtil.BitwiseNot(ref heap, vTypeCode, aSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.ADD:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.Add(ref heap, vTypeCode, aSpan, bSpan, out cSpan);
                            // VmUtil.Push(stack, cBytes, vTypeCode);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.MUL:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.Multiply(vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.POWER:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.Power(vTypeCode, aSpan, bSpan, out cSpan, out var isInvalidExp);
                            if (isInvalidExp)
                            {
                                TriggerRuntimeError(new VirtualRuntimeError
                                {
                                    insIndex = instructionIndex,
                                    type = VirtualRuntimeErrorType.INVALID_POWER,
                                    message = $"invalid-power-expression. ins=[{instructionIndex}] type-code=[{vTypeCode}]"
                                });
                            }
                            
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.DIVIDE:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.Divide(vTypeCode, aSpan, bSpan, out cSpan, out var isDivideByZero);
                            if (isDivideByZero)
                            {
                                TriggerRuntimeError(new VirtualRuntimeError
                                {
                                    insIndex = instructionIndex,
                                    type = VirtualRuntimeErrorType.DIVIDE_BY_ZERO,
                                    message = $"divide-by-zero. ins=[{instructionIndex}] type-code=[{vTypeCode}], numerator-value=[{VmUtil.ConvertValueToDisplayString(vTypeCode, this, ref bSpan)}] numerator-bytes=[{string.Join(",", bSpan.ToArray())}]"
                                });
                            }
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.MOD:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.Mod(vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.LOGICAL_2:
                            var aTypeCode = stack.Pop();
                            VmUtil.ReadSpan(ref stack, aTypeCode, out aSpan);
                            
                            var bTypeCode = stack.Pop();
                            VmUtil.ReadSpan(ref stack, bTypeCode, out bSpan);

                            VmUtil.CastInlineSpan(aSpan, aTypeCode, TypeCodes.INT, ref aSpan);
                            VmUtil.CastInlineSpan(bSpan, bTypeCode, TypeCodes.INT, ref bSpan);
                            
                            int aInt = BitConverter.ToInt32(aSpan.ToArray(), 0) > 0 ? 1 : 0;
                            int bInt = BitConverter.ToInt32(bSpan.ToArray(), 0) > 0 ? 1 : 0;
                            
                            VmUtil.PushSpan(ref stack, BitConverter.GetBytes(aInt), TypeCodes.INT);
                            VmUtil.PushSpan(ref stack, BitConverter.GetBytes(bInt), TypeCodes.INT);
                            
                            break;
                        case OpCodes.GT:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.GreaterThan(vTypeCode, bSpan, aSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.GTE:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.GreaterThanOrEqualTo(vTypeCode, bSpan, aSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.LT:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.GreaterThan(vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.LTE:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.GreaterThanOrEqualTo(vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.EQ:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.EqualTo(ref stack, ref heap, vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, TypeCodes.INT);
                            break;
                        case OpCodes.COPY_HEAP_MEM:
                            VmUtil.ReadAsVmPtr(ref stack, out var memReadPtr);
                            if (!heap.TryGetAllocation(memReadPtr, out var allocation))
                            {
                                TriggerRuntimeError(new VirtualRuntimeError
                                {
                                    insIndex = instructionIndex,
                                    type = VirtualRuntimeErrorType.INVALID_MEMORY_COPY,
                                    message = $"ins=[{instructionIndex}] "
                                });
                                break;
                            }
                            
                            heap.Allocate(ref allocation.format, allocation.length, out var memWritePtr);
                            
                            heap.Copy(memReadPtr, memWritePtr, allocation.length);
                            
                            bBytes = VmPtr.GetBytes(ref memWritePtr);
                            VmUtil.PushSpan(ref stack, bBytes, TypeCodes.PTR_HEAP);

                            break;
                        case OpCodes.STORE:
                            // read a register location, which is always 1 byte.
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out addr);

                            VmUtil.ReadSpanAsUInt(ref stack, out data);
                            scope.dataRegisters[addr] = data;
                            scope.typeRegisters[addr] = typeCode;
                            scope.insIndexes[addr] = instructionIndex - 1; // minus one because the instruction has already been advanced. 
                            globalScope.flags[addr] = 0;
                            
                            break;
                        case OpCodes.STORE_PTR:

                            // read a register location, which is always 1 byte.
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out addr);

                            VmUtil.ReadSpanAsUInt(ref stack, out data);

                            scope.dataRegisters[addr] = data;
                            scope.typeRegisters[addr] = typeCode;
                            scope.insIndexes[addr] = instructionIndex - 1; // minus one because the instruction has already been advanced.
                            scope.flags[addr] = VirtualScope.FLAG_PTR;

                            if (heap.allocsSinceCollect >= sweepInterval)
                            {
                                CollectGarbage();
                            }

                            break;
                        case OpCodes.STORE_PTR_GLOBAL:

                            // read a register location, which is always 1 byte.
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out addr);

                            VmUtil.ReadSpanAsUInt(ref stack, out data);

                            globalScope.dataRegisters[addr] = data;
                            globalScope.typeRegisters[addr] = typeCode;
                            globalScope.insIndexes[addr] = instructionIndex - 1; // minus one because the instruction has already been advanced.
                            globalScope.flags[addr] = VirtualScope.FLAG_PTR | VirtualScope.FLAG_GLOBAL;

                            if (heap.allocsSinceCollect >= sweepInterval)
                            {
                                CollectGarbage();
                            }

                            break;
                        case OpCodes.STORE_GLOBAL:
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out addr);
                            VmUtil.ReadSpanAsUInt(ref stack, out data);
                            globalScope.dataRegisters[addr] = data;
                            globalScope.typeRegisters[addr] = typeCode;
                            globalScope.flags[addr] = VirtualScope.FLAG_GLOBAL;
                            globalScope.insIndexes[addr] = instructionIndex - 1; // minus one because the instruction has already been advanced. 

                            break;
                        case OpCodes.LOAD:
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out addr);

                            typeCode = scope.typeRegisters[addr];
                            data = scope.dataRegisters[addr];
                            size = TypeCodes.GetByteSize(typeCode);
                            aBytes = BitConverter.GetBytes(data);
                            stack.PushSpanAndType(new ReadOnlySpan<byte>(aBytes), typeCode, size);
                            
                            break;
                        case OpCodes.LOAD_PTR:
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out addr);

                            typeCode = TypeCodes.PTR_HEAP;// globalScope.typeRegisters[addr];
                            data = scope.dataRegisters[addr];
                            size = TypeCodes.GetByteSize(typeCode);
                            aBytes = BitConverter.GetBytes(data);
                            stack.PushSpanAndType(new ReadOnlySpan<byte>(aBytes), typeCode, size);

                            break;
                        case OpCodes.LOAD_PTR_GLOBAL:
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out addr);

                            typeCode = TypeCodes.PTR_HEAP;// globalScope.typeRegisters[addr];
                            data = globalScope.dataRegisters[addr];
                            size = TypeCodes.GetByteSize(typeCode);
                            aBytes = BitConverter.GetBytes(data);
                            stack.PushSpanAndType(new ReadOnlySpan<byte>(aBytes), typeCode, size);

                            break;
                        case OpCodes.LOAD_GLOBAL:
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out addr);

                            typeCode = globalScope.typeRegisters[addr];
                            data = globalScope.dataRegisters[addr];
                            size = TypeCodes.GetByteSize(typeCode);
                            aBytes = BitConverter.GetBytes(data);
                            stack.PushSpanAndType(new ReadOnlySpan<byte>(aBytes), typeCode, size);
                            
                            break;
  
                        case OpCodes.ALLOC:
                            
                            // read the heap-type format
                            VmUtil.ReadAsTypeFormat(ref stack, out var format);
                            
                            // next value is an int, we know this.
                            VmUtil.ReadAsInt(ref stack, out var allocLength);
                            heap.Allocate(ref format, allocLength, out var allocPtr);
                            // push the address onto the stack
                            bBytes = VmPtr.GetBytes(ref allocPtr);
                            VmUtil.PushSpan(ref stack, bBytes, TypeCodes.PTR_HEAP);
                            
                            break;
                        case OpCodes.DISCARD:
                            stack.Pop();
                            break;
                        case OpCodes.DISCARD_TYPED:
                            // use an if-statement, because the compiler doesn't know (care)
                            //  if a function was a void-function or a value-function
                            //  and will always stick the DISCARD on anyway (sheesh)
                            if (stack.ptr > 0)
                            {
                                VmUtil.ReadSpan(ref stack, out _, out _);
                            }
                            break;
                        case OpCodes.WRITE:
                            VmUtil.WriteToHeap(ref stack, ref heap, false);
                            if (heap.allocsSinceCollect >= sweepInterval)
                            {
                                CollectGarbage();
                            }
                            break;
                        case OpCodes.WRITE_PTR:
                            VmUtil.WriteToHeap(ref stack, ref heap, true);
                            if (heap.allocsSinceCollect >= sweepInterval)
                            {
                                CollectGarbage();
                            }
                            break;
                        case OpCodes.BOUNDS_CHECK:
                            VmUtil.ReadAsInt(ref stack, out var ceilingValue);
                            VmUtil.ReadAsInt(ref stack, out var indexValue);
                            if (indexValue < 0 || indexValue >= ceilingValue)
                            {
                                TriggerRuntimeError(new VirtualRuntimeError
                                {
                                    insIndex = instructionIndex,
                                    type = VirtualRuntimeErrorType.INVALID_ADDRESS,
                                    message = $"invalid-address. ins=[{instructionIndex}] index=[{indexValue}] min=[0] max=[{ceilingValue}]"
                                });
                            }
                            
                            break;
                        case OpCodes.READ:
                        {
                            VmUtil.ReadAsVmPtr(ref stack, out var readPtr);
                            VmUtil.ReadAsInt(ref stack, out var readLength);
                            heap.ReadSpan(readPtr, readLength, out var readSpan);
                            stack.PushSpan(readSpan, readLength);
                            break;
                        }
                        // case OpCodes.LENGTH:
                        //     VmUtil.ReadAsInt(ref stack, out var readLengthPtr);
                        //     heap.GetAllocationSize(readLengthPtr, out var readAllocLength);
                        //     VmUtil.PushSpan(ref stack, BitConverter.GetBytes(readAllocLength), TypeCodes.INT);
                        //     break;
                        case OpCodes.CALL_HOST:
                            VmUtil.ReadAsInt(ref stack, out var hostMethodPtr);
                            hostMethods.FindMethod(hostMethodPtr, out var method);

                            // Per-command invocation counting. We tally every
                            // CALL_HOST in test mode regardless of mock state
                            // so `call count <cmd>` works even before any
                            // mock is installed. Outside test mode the count
                            // is unused, so skip the dictionary work.
                            if (isTestExecution)
                            {
                                hostCallCounts ??= new Dictionary<int, int>();
                                hostCallCounts.TryGetValue(hostMethodPtr, out var prevCount);
                                hostCallCounts[hostMethodPtr] = prevCount + 1;
                            }

                            if (mockTable != null && mockTable.TryGetValue(hostMethodPtr, out var mock))
                            {
                                if (mock.kind == 3)
                                {
                                    // Phase B: mock body is a bytecode block.
                                    // The args are still on the stack — the
                                    // body itself pops and binds them as
                                    // locals in a fresh scope. We push the
                                    // method-call return frame so the body's
                                    // RETURN lands us back here.
                                    methodStack.Push(new JumpHistoryData
                                    {
                                        fromIns = instructionIndex,
                                        toIns = mock.bodyAddr
                                    });
                                    instructionIndex = mock.bodyAddr;
                                    break;
                                }

                                // Legacy path (Phase A): pop the args off the
                                // stack as the real executor would, then
                                // synthesize the behavior.
                                if (method.args != null)
                                {
                                    for (var ai = method.args.Length - 1; ai >= 0; ai--)
                                    {
                                        if (method.args[ai].isVmArg) continue;
                                        VmUtil.ReadValueAny(this, default, out _, out _, out _, allowOptional: true);
                                    }
                                }

                                if (mock.kind == 1)
                                {
                                    // returns: push the recorded value
                                    VmUtil.PushSpan(ref stack, mock.returnBytes, mock.returnTypeCode);
                                }
                                else if (mock.kind == 2)
                                {
                                    // Forbid: same shape as a failing assert.
                                    // Capture the call stack, build a TestFailure
                                    // carrying the user's reason (if supplied),
                                    // and redirect to the unwind trampoline so
                                    // defers in every live scope drain before
                                    // the test runner reports the result.
                                    // Re-entrancy guard: if a prior failure is
                                    // already recorded (e.g., a deferred body
                                    // re-fires forbid or assert), just halt
                                    // and keep the first failure.
                                    if (assertionFailure != null)
                                    {
                                        instructionIndex = int.MaxValue;
                                        break;
                                    }
                                    assertionFailure = new TestFailure
                                    {
                                        sourceText = "forbidden command was called: " + method.name,
                                        reason = mock.forbidReason ?? "",
                                        instructionIndex = instructionIndex,
                                        callStack = CaptureCallStack()
                                    };
                                    instructionIndex = mock.forbidTrampolineAddr > 0
                                        ? mock.forbidTrampolineAddr
                                        : int.MaxValue;
                                }
                                // kind == 0 (void): nothing else to do; args are gone
                            }
                            else
                            {
                                HostMethodUtil.Execute(method, this);
                            }

                            break;

                        case OpCodes.CALL_HOST_REAL:
                        {
                            // `passthrough` inside a mock body: dispatch
                            // to the real command, never to the mock. We
                            // don't bump hostCallCounts here because the
                            // outer CALL_HOST that routed into the mock
                            // already counted this invocation.
                            //
                            // Scope dance: the body's PUSH_SCOPE made the
                            // mock body's locals the current scope. The
                            // real host writes ref args via
                            // `vm.dataRegisters[addr]` (current scope), so
                            // for PTR_REG addresses that point to the
                            // caller's registers to land correctly, we
                            // need the caller's scope to BE the current
                            // scope during the call. Temporarily pop the
                            // body scope, run the host, then put it back.
                            // Body-local arrays remain valid because
                            // VirtualScope.dataRegisters is a managed
                            // reference and survives the by-value copy.
                            VmUtil.ReadAsInt(ref stack, out var realHostMethodPtr);
                            hostMethods.FindMethod(realHostMethodPtr, out var realMethod);

                            var savedBodyScope = scopeStack.buffer[scopeStack.ptr - 1];
                            scopeStack.ptr--;
                            scope = scopeStack.buffer[scopeStack.ptr - 1];

                            HostMethodUtil.Execute(realMethod, this);

                            scopeStack.buffer[scopeStack.ptr] = savedBodyScope;
                            scopeStack.ptr++;
                            scope = savedBodyScope;
                            break;
                        }

                        case OpCodes.GATHER_ARRAY:
                        {
                            // Inverse of SPREAD_ARRAY. Inline element type
                            // byte; stack has `[..., elemN, ..., elem1, count]`
                            // (count on top — same shape a `params` arg
                            // produces). Pops count, then pops `count`
                            // typed values, materializes a heap block,
                            // pushes the PTR_HEAP.
                            var gatherElemTc = Advance();
                            var gatherElemSize = TypeCodes.GetByteSize(gatherElemTc);
                            VmUtil.ReadAsInt(ref stack, out var gatherCount);
                            var gatherBytes = new byte[gatherCount * gatherElemSize];
                            for (var gi = 0; gi < gatherCount; gi++)
                            {
                                // Each element has [data_bytes][type_byte];
                                // pop type, then data. We trust the type
                                // matches what the inline byte says (caller
                                // sets it from the params arg metadata).
                                stack.Pop(); // discard type code
                                for (var gb = gatherElemSize - 1; gb >= 0; gb--)
                                {
                                    gatherBytes[gi * gatherElemSize + gb] = stack.Pop();
                                }
                            }
                            var gatherFormat = new HeapTypeFormat
                            {
                                typeCode = gatherElemTc,
                                typeFlags = HeapTypeFormat.CreateArrayFlag(1)
                            };
                            heap.Allocate(ref gatherFormat, gatherBytes.Length, out var gatherPtr);
                            heap.Write(gatherPtr, gatherBytes.Length, gatherBytes);
                            var gatherPtrBytes = VmPtr.GetBytes(ref gatherPtr);
                            VmUtil.PushSpan(ref stack, gatherPtrBytes, TypeCodes.PTR_HEAP);
                            break;
                        }
                        case OpCodes.LENGTH:
                        {
                            // Inline 1-byte element size. Pops a heap ptr
                            // (or STRING-typed heap ptr — interned strings
                            // are tagged STRING after their CAST), reads
                            // the allocation size, divides by the element
                            // size, pushes the count as an int.
                            var lenElemSize = Advance();
                            stack.Pop(); // discard the type code (PTR_HEAP, STRING, etc.)
                            var lenPtrBytes = new byte[8];
                            for (var lb = 7; lb >= 0; lb--) lenPtrBytes[lb] = stack.Pop();
                            var lenPtr = VmPtr.FromBytes(lenPtrBytes);
                            heap.TryGetAllocationSize(lenPtr, out var lenBytes);
                            var lenCount = lenElemSize > 0 ? lenBytes / lenElemSize : 0;
                            VmUtil.PushSpan(ref stack,
                                BitConverter.GetBytes(lenCount),
                                TypeCodes.INT);
                            break;
                        }
                        case OpCodes.SPREAD_ARRAY:
                        {
                            // Pops a Fade-array heap ptr, then pushes each
                            // element as a typed value (in reverse, so the
                            // first element ends up second-from-top), then
                            // pushes the element count as an int. The
                            // overall stack shape after this matches what a
                            // `params` arg expects from the host-method
                            // dispatcher: [..., elemN, ..., elem1, count].
                            var spreadElemTc = Advance();
                            var spreadElemSize = TypeCodes.GetByteSize(spreadElemTc);
                            VmUtil.ReadAsVmPtr(ref stack, out var spreadPtr);
                            heap.TryGetAllocationSize(spreadPtr, out var spreadBytes);
                            var spreadCount = spreadElemSize > 0 ? spreadBytes / spreadElemSize : 0;
                            if (spreadCount > 0)
                            {
                                heap.ReadSpan(spreadPtr, spreadBytes, out var spreadSpan);
                                // Push elements LIFO so the receiver reads
                                // them back in declaration order — same as
                                // an inline `Foo(1,2,3)` call would produce.
                                for (var ei = spreadCount - 1; ei >= 0; ei--)
                                {
                                    VmUtil.PushSpan(ref stack, spreadSpan.Slice(ei * spreadElemSize, spreadElemSize), spreadElemTc);
                                }
                            }
                            VmUtil.PushSpan(ref stack,
                                BitConverter.GetBytes(spreadCount),
                                TypeCodes.INT);
                            break;
                        }
                        case OpCodes.STORE_REF:
                        {
                            // Inline 4-byte register address (a body-local).
                            // Stack at dispatch (top → bottom):
                            //   ptr type code (1 byte), 8 bytes register addr.
                            // Unlike STORE_PTR, the type comes from the
                            // stack — necessary because VM-state typeCode
                            // has been clobbered by intervening opcodes.
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out var refStoreAddr);
                            var ptrTc = stack.Pop();
                            var ptrBytes = new byte[8];
                            for (var sb = 7; sb >= 0; sb--) ptrBytes[sb] = stack.Pop();
                            var ptrData = BitConverter.ToUInt64(ptrBytes, 0);
                            scope.dataRegisters[refStoreAddr] = ptrData;
                            scope.typeRegisters[refStoreAddr] = ptrTc;
                            scope.flags[refStoreAddr] = VirtualScope.FLAG_PTR;
                            break;
                        }
                        case OpCodes.LOAD_REF:
                        {
                            // Inline 4-byte register address (a body-local
                            // holding a PTR_REG / PTR_GLOBAL_REG). Read
                            // through that pointer into the caller's scope
                            // (or global) and push the typed value found
                            // there. The body's PUSH_SCOPE pushed a new
                            // scope after CALL_HOST routed here, so the
                            // caller's scope sits one slot below current.
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out var refReadAddr);
                            var refRegAddr2 = scope.dataRegisters[refReadAddr];
                            var refPtrType2 = scope.typeRegisters[refReadAddr];

                            ulong valData;
                            byte valType;
                            if (refPtrType2 == TypeCodes.PTR_GLOBAL_REG)
                            {
                                valData = globalScope.dataRegisters[refRegAddr2];
                                valType = globalScope.typeRegisters[refRegAddr2];
                            }
                            else
                            {
                                ref var callerScope2 = ref scopeStack.buffer[scopeStack.ptr - 2];
                                valData = callerScope2.dataRegisters[refRegAddr2];
                                valType = callerScope2.typeRegisters[refRegAddr2];
                            }
                            var valSize = TypeCodes.GetByteSize(valType);
                            var valBytes = BitConverter.GetBytes(valData);
                            stack.PushSpanAndType(new ReadOnlySpan<byte>(valBytes), valType, valSize);
                            break;
                        }
                        case OpCodes.WRITE_REF:
                        {
                            // Inline 4-byte register address (a body-local
                            // holding the caller's ref pointer). Pops a
                            // typed value from the stack and writes it
                            // through the pointer into the caller's scope.
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out var refLocalAddr);

                            // Body-local holds: dataRegister = 8 bytes of
                            // the caller's register address, typeRegister =
                            // PTR_REG or PTR_GLOBAL_REG.
                            var refRegAddr = scope.dataRegisters[refLocalAddr];
                            var refPtrTypeCode = scope.typeRegisters[refLocalAddr];

                            // Peek the value's type code from the stack
                            // before reading the data, so we can stamp it
                            // back into the caller's register.
                            var valTypeCode = stack.buffer[stack.ptr - 1];
                            VmUtil.ReadSpanAsUInt(ref stack, out var refData);

                            if (refPtrTypeCode == TypeCodes.PTR_GLOBAL_REG)
                            {
                                globalScope.dataRegisters[refRegAddr] = refData;
                                globalScope.typeRegisters[refRegAddr] = valTypeCode;
                            }
                            else
                            {
                                // PTR_REG: write into the caller's scope.
                                // The body's PUSH_SCOPE pushed a new scope on
                                // top after CALL_HOST routed here, so the
                                // caller's scope sits one slot below.
                                ref var callerScope = ref scopeStack.buffer[scopeStack.ptr - 2];
                                callerScope.dataRegisters[refRegAddr] = refData;
                                callerScope.typeRegisters[refRegAddr] = valTypeCode;
                            }
                            break;
                        }
                        case OpCodes.MOCK_INSTALL:
                        {
                            // Stack at dispatch (bottom→top): bodyAddr (int), commandId (int).
                            VmUtil.ReadAsInt(ref stack, out var installCmdId);
                            VmUtil.ReadAsInt(ref stack, out var installBodyAddr);
                            mockTable ??= new Dictionary<int, MockBehavior>();
                            mockTable[installCmdId] = new MockBehavior
                            {
                                kind = 3,
                                bodyAddr = installBodyAddr
                            };
                            break;
                        }
                        case OpCodes.MOCK_VOID:
                        {
                            VmUtil.ReadAsInt(ref stack, out var voidId);
                            mockTable ??= new Dictionary<int, MockBehavior>();
                            mockTable[voidId] = new MockBehavior { kind = 0 };
                            break;
                        }
                        case OpCodes.MOCK_RETURNS:
                        {
                            // Stack top: typed return value; below: commandId.
                            VmUtil.ReadSpan(ref stack, out var retType, out var retSpan);
                            var retBytes = retSpan.ToArray();
                            VmUtil.ReadAsInt(ref stack, out var retId);
                            mockTable ??= new Dictionary<int, MockBehavior>();
                            mockTable[retId] = new MockBehavior
                            {
                                kind = 1,
                                returnTypeCode = retType,
                                returnBytes = retBytes
                            };
                            break;
                        }
                        case OpCodes.MOCK_FORBID:
                        {
                            // Stack at dispatch (bottom→top):
                            //   reason (string), trampolineAddr (int), commandId (int)
                            VmUtil.ReadAsInt(ref stack, out var forbidId);
                            VmUtil.ReadAsInt(ref stack, out var forbidTrampoline);
                            var forbidReason = PopAssertString();
                            mockTable ??= new Dictionary<int, MockBehavior>();
                            mockTable[forbidId] = new MockBehavior
                            {
                                kind = 2,
                                forbidReason = forbidReason,
                                forbidTrampolineAddr = forbidTrampoline
                            };
                            break;
                        }
                        case OpCodes.MOCK_CLEAR:
                        {
                            VmUtil.ReadAsInt(ref stack, out var clearId);
                            mockTable?.Remove(clearId);
                            break;
                        }
                        case OpCodes.MOCK_CLEAR_ALL:
                            mockTable?.Clear();
                            break;
                        
                        case OpCodes.NOOP:
                            // do nothing! Its a no-op!
                            break;
                        
                        case OpCodes.EXPLODE:
                            TriggerRuntimeError(new VirtualRuntimeError
                            {
                                type = VirtualRuntimeErrorType.EXPLODE
                            });
                            break;
                        case OpCodes.TOKENIZE:
                            // hmm!

                            if (tokenReplacements == null)
                            {
                                TriggerRuntimeError(new VirtualRuntimeError
                                {
                                    type = VirtualRuntimeErrorType.CANNOT_TOKENIZE_WITHOUT_HIGHER_CONTEXT
                                });
                                break;
                            }

                            var replacement = new TokenReplacement();
                            tokenReplacements.Add(replacement);
                            // pull off the number of substitutions
                            VmUtil.ReadAsInt(ref stack, out replacement.substitutionCount);
                            
                            // read the line-replacement
                            // VmUtil.ReadValueString(this, "!", out var replacement, out var state, out var addr2);

                            // read the end-index and start-index of the tokenization expression
                            VmUtil.ReadAsInt(ref stack, out replacement.tokenBlockIndex);
                            VmUtil.ReadAsInt(ref stack, out replacement.tokenEndIndex);
                            VmUtil.ReadAsInt(ref stack, out replacement.tokenStartIndex);
                            
                            
                            for (var x = 0; x < replacement.substitutionCount; x++)
                            {
                                var substitution = new TokenSubstitutionReplacement();
                                
                                // for each substitution, read where in the final string it will go
                                // var wasHaunted = stack.Pop();
                                VmUtil.ReadAsInt(ref stack, out a);
                                substitution.transitiveTypeFlags = (TransitiveTypeFlags)a;
                                
                                // check if the substitution should be stringified. 
                                VmUtil.ReadAsInt(ref stack, out a);
                                substitution.isStringify = a > 0;
                                
                                VmUtil.ReadAsInt(ref stack, out substitution.tokenEndIndex);
                                VmUtil.ReadAsInt(ref stack, out substitution.tokenStartIndex);
                                VmUtil.ReadAsInt(ref stack, out substitution.tokenIndex);
                                
                                // peek the type code to stick into the replacement...
                                substitution.typeCode = stack.Peek(); 
                                // then read the actual expression 
                                VmUtil.ReadValueAny(this, null, out substitution.raw, out var valState, out var valAddr);

                                replacement.substitutionReplacements.Add(substitution);
                                // var next = replacement.Insert(local, val.ToString());
                                // replacement = next;
                            }
                            
                            // tokenReplacements.Add(new TokenReplacement
                            // {
                            //     line = replacement
                            // });
                            // TODO: somehow, get the substituted token data and stick it into the tokenReplacements block.
                            //  then it is up to some higher context to jam those tokens together, and re-render the program again. 
                            
                            break;
                        case OpCodes.BREAKPOINT:
                            break;
                        case OpCodes.RUNTO:
                            // Stack at dispatch: [..., maxCycles, target]. Pop target first.
                            VmUtil.ReadAsInt(ref stack, out var runtoTarget);
                            VmUtil.ReadAsInt(ref stack, out var runtoMaxCycles);
                            // The test-resume IP is the very next instruction after this RUNTO.
                            // (instructionIndex has already been incremented past the RUNTO opcode.)
                            runtoStack.Push(new RuntoFrame
                            {
                                targetAddr = runtoTarget,
                                testResumeIp = instructionIndex,
                                cyclesRemaining = runtoMaxCycles
                            });
                            // Switch execution to wherever the program is currently paused.
                            instructionIndex = programResumeIP;
                            break;
                        case OpCodes.RUNTO_YIELD:
                            // The compiler emits RUNTO_YIELD after every label that's a runto target.
                            // We're exactly one instruction past the label here. If the runtoStack top
                            // matches our address, yield back to the test. Otherwise fall through.
                            //
                            // The "match" is: the target address that the test asked for == the address
                            // immediately AFTER the RUNTO_YIELD opcode (i.e., the body of the program
                            // resuming at the next real instruction). The compiler records the target
                            // as that post-yield address.
                            if (runtoStack.Count > 0 && runtoStack.buffer[runtoStack.ptr - 1].targetAddr == instructionIndex)
                            {
                                var frame = runtoStack.Pop();
                                // Save where the program is now so the next runto can resume from here.
                                programResumeIP = instructionIndex;
                                instructionIndex = frame.testResumeIp;
                            }
                            // else fall through; this label wasn't the targeted one.
                            break;
                        case OpCodes.ASSERT_FAIL:
                        {
                            // Data stack at dispatch (bottom → top):
                            //   reason (string), sourceText (string), trampolineAddr (int)
                            // Strings come from the LiteralStringExpression path
                            // ([8 ptr bytes][STRING type code]); interned strings get
                            // CAST to STRING after the PTR push, variable refs push the
                            // same shape with a heap ptr. We accept either STRING or
                            // PTR_HEAP. trampolineAddr is the compiler-baked address of
                            // the assert-unwind trampoline, used in test mode only.
                            VmUtil.ReadAsInt(ref stack, out var trampolineAddr);
                            var text = PopAssertString();
                            var reasonText = PopAssertString();
                            if (isTestExecution)
                            {
                                // Re-entrancy guard: if a deferred body that we're
                                // running as part of unwinding contains its own
                                // failing assert, keep the first failure and halt
                                // instead of restarting the trampoline.
                                if (assertionFailure != null)
                                {
                                    instructionIndex = int.MaxValue;
                                    break;
                                }
                                // Test-mode: record the failure (this path is also
                                // taken when a test runtos into main-program code
                                // that hits an assert) and redirect to the trampoline
                                // so defers in every live scope get drained. Capture
                                // the call chain now; the trampoline doesn't pop
                                // methodStack, but a stable snapshot decouples
                                // downstream consumers from VM state.
                                assertionFailure = new TestFailure
                                {
                                    sourceText = text,
                                    reason = reasonText,
                                    instructionIndex = instructionIndex,
                                    callStack = CaptureCallStack()
                                };
                                instructionIndex = trampolineAddr;
                            }
                            else
                            {
                                // Main-program execution: a failed assert is a
                                // hard runtime error, on par with divide-by-zero.
                                // Defers do NOT run; trampolineAddr is ignored.
                                var hasReason = !string.IsNullOrEmpty(reasonText);
                                var message = hasReason
                                    ? $"assert failed: {text} — {reasonText}"
                                    : $"assert failed: {text}";
                                TriggerRuntimeError(new VirtualRuntimeError
                                {
                                    insIndex = instructionIndex,
                                    type = VirtualRuntimeErrorType.ASSERT_FAILED,
                                    message = message
                                });
                                instructionIndex = int.MaxValue;
                            }
                            break;
                        }
                        default:
                            throw new Exception("Unknown op code: " + ins);
                    }
                    
                    // allow debuggers to pause at known instruction locations. 
                    if (shouldBreakpointCallback != null && shouldBreakpointCallback(instructionIndex))
                    {
                        isSuspendRequested = true;
                    }
                }

                state.vTypeCode = vTypeCode;
                state.typeCode = typeCode;
                state.data = data;
                state.size = size;
                state.insPtr = insPtr;

            }

            return cycles;
        }
        
        public int test = 0;

        public struct RuntoFrame
        {
            public int targetAddr;
            public int testResumeIp;
            public int cyclesRemaining;
        }

        void TriggerRuntimeError(VirtualRuntimeError error)
        {
            // Stamp a call-stack snapshot onto the error unless the caller
            // already provided one. This gives every runtime-error consumer
            // (test runner, future crash reporter, DAP) the same shape used
            // for assert-mode failures.
            if (error.callStack == null)
            {
                error.callStack = CaptureCallStack();
            }
            this.error = error;
            if (shouldThrowRuntimeException)
            {
                throw new VirtualRuntimeException(error);
            }
        }

        /// <summary>
        /// Snapshot the current methodStack into a stable array. Index 0 is the
        /// innermost (most recent) call; the last entry is the outermost.
        /// Used by ASSERT_FAIL test-mode and TriggerRuntimeError to attach a
        /// call-chain to the error, decoupled from later VM state changes.
        /// </summary>
        public JumpHistoryData[] CaptureCallStack()
        {
            var depth = methodStack.Count;
            // Two-pass so we know the visible count up front and can size
            // the array exactly. Launcher frames are filtered — they're
            // internal control flow, not user-visible calls.
            var visible = 0;
            for (var i = 0; i < depth; i++)
            {
                if (!methodStack.buffer[i].isLauncherFrame) visible++;
            }
            var copy = new JumpHistoryData[visible];
            var write = 0;
            for (var i = 0; i < depth; i++)
            {
                var src = methodStack.buffer[depth - 1 - i];
                if (src.isLauncherFrame) continue;
                copy[write++] = src;
            }
            return copy;
        }

        // Pop one Fade string off the data stack and materialize it as a C# string.
        // Used by ASSERT_FAIL. The compiler pushes strings as [8 ptr bytes][type code]
        // and either STRING or PTR_HEAP type codes may appear here. Returns "" if
        // the pointer is null or the read fails.
        private string PopAssertString()
        {
            stack.Pop(); // type code; accepted unconditionally
            var ptrBytes = new byte[8];
            for (var b = 7; b >= 0; b--) ptrBytes[b] = stack.Pop();
            var ptr = VmPtr.FromBytes(ptrBytes);
            try
            {
                if (heap.TryGetAllocationSize(ptr, out var len) && len > 0)
                {
                    heap.Read(ptr, len, out var bytes);
                    // Fade strings are stored as 4-bytes-per-char (uint codepoints).
                    var charCount = len / 4;
                    var chars = new char[charCount];
                    for (var c = 0; c < charCount; c++)
                    {
                        chars[c] = (char)BitConverter.ToUInt32(bytes, c * 4);
                    }
                    return new string(chars);
                }
            }
            catch { /* best-effort recovery; fall through */ }
            return "";
        }
    }

    public class VirtualRuntimeException : Exception
    {
        public VirtualRuntimeError Error { get; }

        public VirtualRuntimeException(VirtualRuntimeError error) : base(error.message)
        {
            Error = error;
        }
    }
}
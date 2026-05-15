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
    }

    public struct VirtualRuntimeError
    {
        public VirtualRuntimeErrorType type;
        public int insIndex;
        public string message;
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
        }

        /// <summary>
        /// Per-VM mock registrations. Keyed by host method id (the index into
        /// <see cref="HostMethodTable.methods"/>). On CALL_HOST the dispatcher
        /// consults this table first; if a registration exists, it pops the
        /// command's args via metadata and synthesizes the mock behavior in
        /// place of the real call.
        /// </summary>
        public Dictionary<int, MockBehavior> mockTable;

        public class MockBehavior
        {
            // 0 = void (skip), 1 = returns (push value), 2 = forbid (assert-fail).
            public byte kind;
            // For kind = Returns: the typed return value to push.
            public byte returnTypeCode;
            public byte[] returnBytes;
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
                heap.IncrementRefCount(ptr); 
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

                            
                            { // clear all references from scope...
                                var vScope = scopeStack.Peek();
                                for (var scopeIndex = 0;
                                     scopeIndex < vScope.insIndexes.Length;
                                     scopeIndex++)
                                {
                                    // var isPtr = vScope.typeRegisters[scopeIndex] == TypeCodes.STRUCT ||
                                    //             vScope.typeRegisters[scopeIndex] == TypeCodes.STRING;
                                    var isPtr = VirtualScope.IsPtr(vScope.flags[scopeIndex]);
                                    var ptr = vScope.dataRegisters[scopeIndex];
                                    if (isPtr && vScope.insIndexes[scopeIndex] > 0)
                                    {
                                        heap.TryDecrementRefCount(ptr);
                                    }
                                }
                            }

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

                            if (scope.insIndexes[addr] > 0)
                            {
                                heap.TryDecrementRefCount(scope.dataRegisters[addr]);
                            }
                        
                            scope.dataRegisters[addr] = data;
                            scope.typeRegisters[addr] = typeCode;
                            scope.insIndexes[addr] = instructionIndex - 1; // minus one because the instruction has already been advanced. 
                            scope.flags[addr] = VirtualScope.FLAG_PTR;
                            
                            heap.IncrementRefCount(data);
                            
                            // TODO: this is not a very good balance of efficiency... 
                            //       the sweeping is costly, and maybe it makes sense to
                            //       do it only every now and then, not on EVERY assign
                            heap.Sweep(); 
                            
                            break;
                        case OpCodes.STORE_PTR_GLOBAL:

                            // read a register location, which is always 1 byte.
                            VmUtil.ReadRegAddress(program, ref instructionIndex, out addr);

                            VmUtil.ReadSpanAsUInt(ref stack, out data);

                            if (globalScope.insIndexes[addr] > 0)
                            {
                                heap.TryDecrementRefCount(globalScope.dataRegisters[addr]);
                            }
                        
                            globalScope.dataRegisters[addr] = data;
                            globalScope.typeRegisters[addr] = typeCode;
                            globalScope.insIndexes[addr] = instructionIndex - 1; // minus one because the instruction has already been advanced. 
                            globalScope.flags[addr] = VirtualScope.FLAG_PTR | VirtualScope.FLAG_GLOBAL;
                            
                            heap.IncrementRefCount(data);
                            heap.Sweep(); 
                            
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
                            break;
                        case OpCodes.WRITE_PTR:
                            VmUtil.WriteToHeap(ref stack, ref heap, true);
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
                            
                            VmUtil.ReadAsVmPtr(ref stack, out var readPtr);
                            VmUtil.ReadAsInt(ref stack, out var readLength);
                            heap.Read(readPtr, readLength, out aBytes);
                            stack.PushSpan(aBytes, readLength);
                            break;
                        // case OpCodes.LENGTH:
                        //     VmUtil.ReadAsInt(ref stack, out var readLengthPtr);
                        //     heap.GetAllocationSize(readLengthPtr, out var readAllocLength);
                        //     VmUtil.PushSpan(ref stack, BitConverter.GetBytes(readAllocLength), TypeCodes.INT);
                        //     break;
                        case OpCodes.CALL_HOST:
                            VmUtil.ReadAsInt(ref stack, out var hostMethodPtr);
                            hostMethods.FindMethod(hostMethodPtr, out var method);

                            if (mockTable != null && mockTable.TryGetValue(hostMethodPtr, out var mock))
                            {
                                // Mocked: pop the args off the stack as the real
                                // executor would, then synthesize the behavior.
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
                                    // forbid: record an assertion failure naming the command
                                    assertionFailure = new TestFailure
                                    {
                                        sourceText = "forbidden command was called: " + method.name,
                                        instructionIndex = instructionIndex
                                    };
                                    instructionIndex = int.MaxValue;
                                }
                                // kind == 0 (void): nothing else to do; args are gone
                            }
                            else
                            {
                                HostMethodUtil.Execute(method, this);
                            }

                            break;

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
                            VmUtil.ReadAsInt(ref stack, out var forbidId);
                            mockTable ??= new Dictionary<int, MockBehavior>();
                            mockTable[forbidId] = new MockBehavior { kind = 2 };
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
                                // so defers in every live scope get drained.
                                assertionFailure = new TestFailure
                                {
                                    sourceText = text,
                                    reason = reasonText,
                                    instructionIndex = instructionIndex
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
            this.error = error;
            if (shouldThrowRuntimeException)
            {
                throw new VirtualRuntimeException(error);
            }
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
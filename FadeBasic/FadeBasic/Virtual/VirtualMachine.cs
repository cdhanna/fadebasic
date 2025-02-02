using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
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
        
        public VirtualScope(int initialCapacity)
        {
            dataRegisters = new ulong[initialCapacity];
            typeRegisters = new byte[initialCapacity];
            insIndexes = new int[initialCapacity];
            flags = new byte[initialCapacity];
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
        INVALID_ADDRESS,
        EXPLODE
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

        public VirtualMachine(IEnumerable<byte> program) : this(program.ToArray())
        {
        }
        public VirtualMachine(byte[] program)
        {
            this.program = program;
            shouldThrowRuntimeException = true;
            globalScope = scope = new VirtualScope(256);
            // scope = new VirtualScope(256);
            scopeStack = new FastStack<VirtualScope>(16);
            methodStack = new FastStack<JumpHistoryData>(16);
            heap = new VmHeap(128);
            scopeStack.Push(globalScope);
            
            instructionIndex = 4;
            internedDataInstructionIndex = BitConverter.ToInt32(program, 0);

            
            ReadInternedData();
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


        private bool isSuspendRequested;
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
            public byte vTypeCode, typeCode, addr, size;
            public ulong data;
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
        }

        public void Execute2(int instructionBatchCount=1000)
        {
            isSuspendRequested = false;

            
            // while (true)
            {

                // the arrays do not need to be held between instruction evaluations
                byte[] aBytes;
                byte[] bBytes;
                ReadOnlySpan<byte> aSpan, bSpan, cSpan;
                
                // these pointer/data values need to be held between instruction evaluations, and therefor stay in the state. 
                byte vTypeCode = state.vTypeCode, typeCode = state.typeCode;
                ulong data = state.data;
                byte addr = state.addr, size = state.size;
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
                            var newScope = new VirtualScope(64);
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
                        case OpCodes.STORE:

                            // read a register location, which is always 1 byte.
                            addr = Advance();
                            VmUtil.ReadSpanAsUInt(ref stack, out data);

                            // 
                            // TODO: need to check if this is an array somehow... 
                            //       but, the compiler baked away that info... 
                            // 
                            
                            // arrays have a special access pattern, 
                            // because there are TWO store operations
                            // this could work, but it is a lot of branching for most cases... Unless the operation is SUPER fast :( 
                            
                           
                            //
                            // var isPointer = typeCode == TypeCodes.STRING || typeCode == TypeCodes.STRUCT;
                            // if (isPointer)
                            // {
                            //     // checking for zero here is a way of checking if the  
                            //     //  register was ever used before. Otherwise, it will
                            //     //  clear the zero-th pointer. 
                            //     if (scope.insIndexes[addr] > 0)
                            //     {
                            //         heap.TryDecrementRefCount(scope.dataRegisters[addr]);
                            //     }
                            // }
                            scope.dataRegisters[addr] = data;
                            scope.typeRegisters[addr] = typeCode;
                            scope.insIndexes[addr] = instructionIndex - 1; // minus one because the instruction has already been advanced. 
                            globalScope.flags[addr] = 0;
                            
                            /*
                             * if the thing we are storing is a pointer, then that is important,
                             * and we should keep track of it.
                             * 
                             */
                            // if (isPointer)
                            // {
                            //     heap.IncrementRefCount(data);
                            //     
                            //     // TODO: this is not a very good balance of efficiency... 
                            //     //       the sweeping is costly, and maybe it makes sense to
                            //     //       do it only every now and then, not on EVERY assign
                            //     heap.Sweep(); 
                            // }

                            break;
                        case OpCodes.STORE_PTR:

                            // read a register location, which is always 1 byte.
                            addr = Advance();
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
                            heap.Sweep(); 
                            
                            break;
                        case OpCodes.STORE_PTR_GLOBAL:

                            // read a register location, which is always 1 byte.
                            addr = Advance();
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
                            addr = Advance();
                            VmUtil.ReadSpanAsUInt(ref stack, out data);
                            globalScope.dataRegisters[addr] = data;
                            globalScope.typeRegisters[addr] = typeCode;
                            globalScope.flags[addr] = VirtualScope.FLAG_GLOBAL;
                            globalScope.insIndexes[addr] = instructionIndex - 1; // minus one because the instruction has already been advanced. 

                            break;
                        case OpCodes.LOAD:
                            addr = Advance();

                            typeCode = scope.typeRegisters[addr];
                            data = scope.dataRegisters[addr];
                            size = TypeCodes.GetByteSize(typeCode);
                            aBytes = BitConverter.GetBytes(data);
                            stack.PushSpanAndType(new ReadOnlySpan<byte>(aBytes), typeCode, size);
                            
                            break;
                        case OpCodes.LOAD_GLOBAL:
                            addr = Advance();

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
                            bBytes = BitConverter.GetBytes(allocPtr);
                            VmUtil.PushSpan(ref stack, bBytes, TypeCodes.INT);
                            
                            break;
                        case OpCodes.DISCARD:
                            stack.Pop();
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
                            
                            VmUtil.ReadAsInt(ref stack, out var readPtr);
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
                            HostMethodUtil.Execute(method, this);
                            
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
                        case OpCodes.BREAKPOINT:
                            break;
                        default:
                            throw new Exception("Unknown op code: " + ins);
                    }
                }

                state.vTypeCode = vTypeCode;
                state.typeCode = typeCode;
                state.data = data;
                state.size = size;
                state.insPtr = insPtr;
            }
            

        }

        void TriggerRuntimeError(VirtualRuntimeError error)
        {
            this.error = error;
            if (shouldThrowRuntimeException)
            {
                throw new VirtualRuntimeException(error);
            }
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
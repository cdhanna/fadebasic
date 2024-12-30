using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;

namespace FadeBasic.Virtual
{


    public class ExecutionState
    {
        public bool isComplete;
        
    }
    
    public struct VirtualScope
    {
        // public readonly int initialCapacity;
        
        // parallel array with dataReg
        public ulong[] dataRegisters;
        public byte[] typeRegisters;
        public int[] insIndexes;
        
        public VirtualScope(int initialCapacity)
        {
            dataRegisters = new ulong[initialCapacity];
            typeRegisters = new byte[initialCapacity];
            insIndexes = new int[initialCapacity];
        }
    }

    public struct JumpHistoryData
    {
        public int fromIns;
        public int toIns;
    }

    
    public class VirtualMachine
    {
        public readonly byte[] program;

        public int instructionIndex;

        
        public FastStack<byte> stack = new FastStack<byte>(256);
        public VmHeap heap;
        
        public HostMethodTable hostMethods;
        public FastStack<JumpHistoryData> methodStack; // TODO: This could also store the index of the scope-stack at the time of the push; so that a debugger could know the scope at the frame.

        public VirtualScope globalScope;
        public FastStack<VirtualScope> scopeStack;

        public VirtualScope scope;

        public IDebugLogger logger;

        public ulong[] dataRegisters => scope.dataRegisters; // TODO: optimize to remove method call Peek()
        public byte[] typeRegisters => scope.typeRegisters;

        public VirtualMachine(IEnumerable<byte> program) : this(program.ToArray())
        {
        }
        public VirtualMachine(byte[] program)
        {
            this.program = program;
            globalScope = scope = new VirtualScope(256);
            scopeStack = new FastStack<VirtualScope>(16);
            methodStack = new FastStack<JumpHistoryData>(16);
            heap = new VmHeap(128);
            scopeStack.Push(globalScope);
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

        public void Execute2(int instructionBatchCount=1000)
        {
            isSuspendRequested = false;
            // while (true)
            {
                byte[] aBytes;
                byte[] bBytes;
                // byte[] cBytes;
                ReadOnlySpan<byte> aSpan, bSpan, cSpan;
                byte aTypeCode = 0, bTypeCode = 0, vTypeCode = 0, typeCode = 0;
                ulong data;
                byte addr, size;
                int insPtr;
                
                // var sw = new Stopwatch();
                var incrementer = instructionBatchCount > 0 ? 1 : 0;
                for (var i = 0; 
                     (instructionBatchCount == 0 || i < instructionBatchCount)
                        && instructionIndex < program.Length 
                        && !isSuspendRequested; 
                     i += incrementer)
                {
                    // if at end of program, exit.
                    // if (instructionIndex >= program.Length)
                    // {
                    //
                    //     break;
                    // }
                    //
                    // if (isSuspendRequested)
                    // {
                    //     break;
                    // }
                    
                    /*
                     * TODO: Supporting a debugger...
                     * 
                     */


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
                            scopeStack.Pop();
                            scope = scopeStack.buffer[scopeStack.ptr -1];
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
                            VmUtil.Divide(vTypeCode, aSpan, bSpan, out cSpan);
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
                            
                            scope.dataRegisters[addr] = data;
                            scope.typeRegisters[addr] = typeCode;
                            scope.insIndexes[addr] = instructionIndex - 1; // minus one because the instruction has already been advanced. 
                            
                            /*
                             * given we know the instruction address,
                             * we could look that instruction up in the DebugData
                             *  and if it exists, attach a variable name to this scope.
                             *
                             * however, from a perf standpoint, I don't want to do that universally unless we
                             * are in a DEBUG mode. problematically, since this is dotnet runtime, there is no
                             * way to have a perfect #compiler symbol thing for runtime.
                             *
                             * a compromise would be to store only the variable's declaration address, and the
                             * debug data and server could use that LATER to re-assemble the information.
                             */

                            break;
                        case OpCodes.STORE_GLOBAL:
                            addr = Advance();
                            VmUtil.ReadSpanAsUInt(ref stack, out data);
                            globalScope.dataRegisters[addr] = data;
                            globalScope.typeRegisters[addr] = typeCode;
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
                            // next value is an int, we know this.
                            VmUtil.ReadAsInt(ref stack, out var allocLength);
                            heap.Allocate(allocLength, out var allocPtr);
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
                            throw new Exception("Kaboom");
                        
                        case OpCodes.BREAKPOINT:
                            break;
                        default:
                            throw new Exception("Unknown op code: " + ins);
                    }
                }
                
            }
            

        }   
    }
}
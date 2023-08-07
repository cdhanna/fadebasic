using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DarkBasicYo.Virtual
{


    public class ExecutionState
    {
        public bool isComplete;
        
    }

    public struct VirtualScope
    {
        public readonly int initialCapacity;
        public ulong[] dataRegisters; // parallel array with typeReg
        public byte[] typeRegisters;  // parallel array with dataReg

        public VirtualScope(int initialCapacity)
        {
            this.initialCapacity = initialCapacity;
            dataRegisters = new ulong[initialCapacity];
            typeRegisters = new byte[initialCapacity];
        }
        
        public void Read(int index, out ulong data, out byte typeCode)
        {
            data = dataRegisters[index];
            typeCode = typeRegisters[index];
        }

        public void Write(int index, ulong data, byte typeCode)
        {
            dataRegisters[index] = data;
            typeRegisters[index] = typeCode;
        }
    }

    
    public class VirtualMachine
    {
        public readonly byte[] program;

        public int instructionIndex;

        // public Stack<byte> stack = new Stack<byte>();
        public FastStack stack = new FastStack(256);
        public VmHeap heap = new VmHeap();
        public HostMethodTable hostMethods= new HostMethodTable();
        public Stack<int> methodStack = new Stack<int>();

        public VirtualScope globalScope = new VirtualScope();
        public Stack<VirtualScope> scopeStack;

        public VirtualScope scope;

        public ulong[] dataRegisters => scope.dataRegisters; // TODO: optimize to remove method call Peek()
        public byte[] typeRegisters => scope.typeRegisters;

        public VirtualMachine(IEnumerable<byte> program) : this(program.ToArray())
        {
        }
        public VirtualMachine(byte[] program)
        {
            this.program = program;
            globalScope = scope = new VirtualScope(256);
            scopeStack = new Stack<VirtualScope>();
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
                byte[] cBytes;
                ReadOnlySpan<byte> aSpan, bSpan, cSpan;
                byte aTypeCode = 0, bTypeCode = 0, vTypeCode = 0, typeCode = 0;
                ulong data;
                byte addr, size;
                int insPtr;

                // var sw = new Stopwatch();
                
                for (var i = 0; i < instructionBatchCount; i++)
                {
                    // if at end of program, exit.
                    if (instructionIndex >= program.Length)
                    {
           
                        break;
                    }
                    
                    if (isSuspendRequested)
                    {
                        break;
                    }


                    var ins = Advance();
                    // sw.Restart();
                    // ulong a = 0, b = 0, aTypeCode = 0, bTypeCode = 0;
                    
                    switch (ins)
                    {
                        case OpCodes.EXPLODE:
                            throw new Exception("Kaboom");
                            break;
                        case OpCodes.PUSH_SCOPE:
                            var newScope = new VirtualScope(64);
                            scopeStack.Push(newScope);
                            scope = newScope;
                            break;
                        case OpCodes.POP_SCOPE:
                            if (scopeStack.Count == 1)
                            {
                                throw new Exception("Cannot pop the global stack");
                            }
                            scopeStack.Pop();
                            scope = scopeStack.Peek();
                            break;
                        case OpCodes.JUMP_TABLE:
                            VmUtil.ReadAsInt(ref stack, out var tableSize);
                            int[] addresses = new int[tableSize];
                            long[] values = new long[tableSize];
                            for (var j = 0; j < tableSize; j++)
                            {
                                VmUtil.ReadSpan(ref stack, out aTypeCode, out aSpan); // the value
                                // VmUtil.Pad(8, aBytes, out aBytes);
                                // var hash = BitConverter.ToInt64(aBytes, 0);
                                VmUtil.ToLongSpan(TypeCodes.GetByteSize(aTypeCode), aSpan, out var hash);
                                VmUtil.ReadAsInt(ref stack, out var caseAddr);
                                addresses[j] = caseAddr;
                                values[j] = hash;
                            }
                            
                            VmUtil.ReadAsInt(ref stack, out var defaultAddr);

                            VmUtil.ReadSpan(ref stack, out bTypeCode, out bSpan);
                            // VmUtil.Pad(8, bBytes, out bBytes);
                            // var key = BitConverter.ToInt64(bBytes, 0);
                            VmUtil.ToLongSpan(TypeCodes.GetByteSize(bTypeCode), bSpan, out var key);

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
                            methodStack.Push(instructionIndex) ;
                            instructionIndex = insPtr;
                            break;
                        case OpCodes.RETURN:
                            if (methodStack.Count != 0)
                            {
                                /*
                                 * the use case to allow a return on an empty stack is
                                 * using GOSUB and not adding an END statement before the program hits the labels.
                                 */
                                instructionIndex = methodStack.Pop();
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
                        case OpCodes.PUSH:
                            typeCode = Advance();
                            size = TypeCodes.GetByteSize(typeCode);
                            stack.PushArray(program, instructionIndex, size);
                            instructionIndex += size;
                            stack.Push(typeCode);
                            
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
                            VmUtil.Add(heap, vTypeCode, aSpan, bSpan, out cSpan);
                            // VmUtil.Push(stack, cBytes, vTypeCode);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.BREAKPOINT:
                            break;
                        case OpCodes.MUL:
                            // throw new NotImplementedException();
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.Multiply(vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.DIVIDE:
                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.Divide(vTypeCode, aSpan, bSpan, out cSpan);
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
                            // throw new NotImplementedException();

                            VmUtil.ReadTwoValues(ref stack, out vTypeCode, out aSpan, out bSpan);
                            VmUtil.EqualTo(vTypeCode, aSpan, bSpan, out cSpan);
                            VmUtil.PushSpan(ref stack, cSpan, vTypeCode);
                            break;
                        case OpCodes.STORE:

                            // read a register location, which is always 1 byte.
                            addr = Advance();
                            VmUtil.ReadSpan(ref stack, out typeCode, out var span);
                            VmUtil.ToULongSpan(TypeCodes.GetByteSize(typeCode), span, out data);
                            scope.dataRegisters[addr] = data;
                            scope.typeRegisters[addr] = typeCode;

                            break;
                        case OpCodes.STORE_GLOBAL:
                            addr = Advance();
                            VmUtil.ReadSpan(ref stack, out typeCode, out var span2);
                            VmUtil.ToULongSpan(TypeCodes.GetByteSize(typeCode), span2, out data);
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
                        case OpCodes.CAST:
                            
                            typeCode = Advance();
                            // continue;
                            VmUtil.Cast(ref stack, typeCode);
                            
                            // ticks[1] = sw.ElapsedTicks;

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
                            VmUtil.WriteToHeap(ref stack, heap, false);
                            break;
                        case OpCodes.WRITE_PTR:
                            VmUtil.WriteToHeap(ref stack, heap, true);
                            break;
                        case OpCodes.READ:
                            
                            VmUtil.ReadAsInt(ref stack, out var readPtr);
                            VmUtil.ReadAsInt(ref stack, out var readLength);
                            heap.Read(readPtr, readLength, out aBytes);
                            stack.PushSpan(aBytes, readLength);
                            // for (var r = readLength -1; r >= 0; r--)
                            // for (var r = 0; r < readLength; r ++)
                            // {
                            //     var b = aBytes[r];
                            //     stack.Push(b);
                            // }
                            
                            break;
                        case OpCodes.LENGTH:
                            VmUtil.ReadAsInt(ref stack, out var readLengthPtr);
                            heap.GetAllocationSize(readLengthPtr, out var readAllocLength);
                            VmUtil.PushSpan(ref stack, BitConverter.GetBytes(readAllocLength), TypeCodes.INT);
                            break;
                        case OpCodes.CALL_HOST:
                            
                            VmUtil.ReadAsInt(ref stack, out var hostMethodPtr);
                            hostMethods.FindMethod(hostMethodPtr, out var method);
                            HostMethodUtil.Execute(method, this);
                            
                            break;
                        
                        case OpCodes.NOOP:
                            // do nothing! Its a no-op!
                            break;
                        default:
                            throw new Exception("Unknown op code: " + ins);
                    }
                }

                // for (var i = 0; i < ticks.Length; i++)
                // {
                //     Console.WriteLine(ticks[i]);
                // }

            }
            

        }   
    }
}
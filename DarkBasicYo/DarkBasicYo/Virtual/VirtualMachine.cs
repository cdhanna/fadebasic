using System;
using System.Collections.Generic;
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

        public Stack<byte> stack = new Stack<byte>();
        public VmHeap heap = new VmHeap();
        public HostMethodTable hostMethods = new HostMethodTable();
        public Stack<int> methodStack = new Stack<int>();

        public VirtualScope globalScope = new VirtualScope();
        public Stack<VirtualScope> scopeStack;

        public VirtualScope scope => scopeStack.Peek();

        public ulong[] dataRegisters => scopeStack.Peek().dataRegisters; // TODO: optimize to remove method call Peek()
        public byte[] typeRegisters => scopeStack.Peek().typeRegisters;

        public VirtualMachine(IEnumerable<byte> program)
        {
            this.program = program.ToArray();
           
            globalScope = new VirtualScope(256);
            scopeStack = new Stack<VirtualScope>();
            scopeStack.Push(globalScope);
            // dataRegisters = new ulong[256];
            // typeRegisters = new byte[256];
            // for (var i = 0; i < dataRegisters.Length; i++)
            // {
            //     dataRegisters[i] = 0;
            //     typeRegisters[i] = 0;
            // }
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
                byte[] aBytes = new byte[] { };
                byte[] bBytes = new byte[] { };
                byte[] cBytes = new byte[] { };
                byte aTypeCode = 0, bTypeCode = 0, vTypeCode = 0, typeCode = 0;
                ulong data = 0;
                byte addr = 0, size =0;
                int insPtr;
                int ad, bd, cd;

                for (var i = 0; i < instructionBatchCount; i++)
                {
                    // if at end of program, exit.
                    if (instructionIndex >= program.Length)
                    {
                        if (stack.Count > 0)
                        {
                            // throw new Exception("Left over stack");
                        }

                        return;
                    }
                    
                    if (isSuspendRequested)
                    {
                        return;
                    }


                    var ins = Advance();
                    // ulong a = 0, b = 0, aTypeCode = 0, bTypeCode = 0;

                    switch (ins)
                    {
                        case OpCodes.EXPLODE:
                            throw new Exception("Kaboom");
                            break;
                        case OpCodes.PUSH_SCOPE:
                            var newScope = new VirtualScope(64);
                            scopeStack.Push(newScope);
                            break;
                        case OpCodes.POP_SCOPE:
                            if (scopeStack.Count == 1)
                            {
                                throw new Exception("Cannot pop the global stack");
                            }
                            scopeStack.Pop();
                            break;
                        case OpCodes.JUMP_TABLE:
                            VmUtil.ReadAsInt(stack, out var tableSize);
                            int[] addresses = new int[tableSize];
                            long[] values = new long[tableSize];
                            for (var j = 0; j < tableSize; j++)
                            {
                                VmUtil.Read(stack, out aTypeCode, out aBytes); // the value
                                VmUtil.Pad(8, aBytes, out aBytes);
                                var hash = BitConverter.ToInt64(aBytes, 0);
                                VmUtil.ReadAsInt(stack, out var caseAddr);
                                addresses[j] = caseAddr;
                                values[j] = hash;
                            }
                            
                            VmUtil.ReadAsInt(stack, out var defaultAddr);

                            VmUtil.Read(stack, out bTypeCode, out bBytes);
                            VmUtil.Pad(8, bBytes, out bBytes);
                            var key = BitConverter.ToInt64(bBytes, 0);

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
                            VmUtil.ReadAsInt(stack, out insPtr);
                            instructionIndex = insPtr;
                            break;
                        case OpCodes.JUMP_GT_ZERO:
                            VmUtil.ReadAsInt(stack, out insPtr);
                            VmUtil.ReadAsInt(stack, out var jumpValue);
                            if (jumpValue > 0)
                            {
                                instructionIndex = insPtr;
                            }
                            break;
                        
                        case OpCodes.JUMP_ZERO:
                            VmUtil.ReadAsInt(stack, out insPtr);
                            VmUtil.ReadAsInt(stack, out var jumpValue3);
                            if (jumpValue3 == 0)
                            {
                                instructionIndex = insPtr;
                            }
                            break;
                        case OpCodes.JUMP_HISTORY:
                            // the next instruction is the instruction ptr
                            VmUtil.ReadAsInt(stack, out insPtr);
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
                            VmUtil.Read(stack, out typeCode, out aBytes);
                            VmUtil.Push(stack, aBytes, typeCode);
                            VmUtil.Push(stack, aBytes, typeCode);
                            break;
                        case OpCodes.BPUSH:
                            var code = Advance();
                            stack.Push(code);
                            break;
                        case OpCodes.PUSH:
                            typeCode = Advance();

                            size = TypeCodes.GetByteSize(typeCode);
                            for (var n = 0; n < size; n ++)
                            {
                                var value = Advance();
                                stack.Push(value);
                            }
                            
                            stack.Push(typeCode);
                            break;
                        case OpCodes.PUSH_TYPELESS:
                            typeCode = Advance();

                            size = TypeCodes.GetByteSize(typeCode);
                            for (var n = 0; n < size; n ++)
                            {
                                var value = Advance();
                                stack.Push(value);
                            }
                            
                            break;
                        case OpCodes.NOT:
                            VmUtil.Read(stack, out typeCode, out aBytes);
                            VmUtil.Not(typeCode, aBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, typeCode);
                            break;
                        case OpCodes.MIN_MAX_PUSH:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.GetMinMax(vTypeCode, aBytes, bBytes, out var needsFlip);
                            if (needsFlip)
                            {
                                VmUtil.Push(stack, aBytes, typeCode);
                                VmUtil.Push(stack, bBytes, typeCode);
                            }
                            else
                            {
                                VmUtil.Push(stack, bBytes, typeCode);
                                VmUtil.Push(stack, aBytes, typeCode);
                            }
                            // VmUtil.Push(stack, cBytes, typeCode);
                            break;
                        case OpCodes.ADD:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.Add(heap, vTypeCode, aBytes, bBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, vTypeCode);
                            break;
                        case OpCodes.BREAKPOINT:
                            break;
                        case OpCodes.MUL:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.Multiply(vTypeCode, aBytes, bBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, vTypeCode);
                            break;
                        case OpCodes.DIVIDE:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.Divide(vTypeCode, aBytes, bBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, vTypeCode);
                            break;
                        case OpCodes.GT:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.GreaterThan(vTypeCode, bBytes, aBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, vTypeCode);
                            break;
                        case OpCodes.GTE:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.GreaterThanOrEqualTo(vTypeCode, bBytes, aBytes, out cBytes);
                           
                            VmUtil.Push(stack, cBytes, vTypeCode);
                            break;
                        case OpCodes.LT:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.GreaterThan(vTypeCode, aBytes, bBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, vTypeCode);
                            break;
                        case OpCodes.LTE:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.GreaterThanOrEqualTo(vTypeCode, aBytes, bBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, vTypeCode);
                            break;
                        case OpCodes.EQ:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.EqualTo(vTypeCode, aBytes, bBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, vTypeCode);
                            break;
                        case OpCodes.STORE:
                            // read a register location, which is always 1 byte.
                            addr = Advance();
                            VmUtil.Read(stack, out typeCode, out aBytes);
                            VmUtil.Pad(8, aBytes, out aBytes);
                            data = BitConverter.ToUInt64(aBytes, 0);

                            scope.dataRegisters[addr] = data;
                            scope.typeRegisters[addr] = typeCode;
                            break;
                        case OpCodes.STORE_GLOBAL:
                            addr = Advance();
                            VmUtil.Read(stack, out typeCode, out aBytes);
                            VmUtil.Pad(8, aBytes, out aBytes);
                            data = BitConverter.ToUInt64(aBytes, 0);

                            globalScope.dataRegisters[addr] = data;
                            globalScope.typeRegisters[addr] = typeCode;
                            break;
                        case OpCodes.LOAD:
                            addr = Advance();

                            typeCode = scope.typeRegisters[addr];
                            data = scope.dataRegisters[addr];
                            size = TypeCodes.GetByteSize(typeCode);
                            aBytes = BitConverter.GetBytes(data);
                            for (var n = size -1 ; n >= 0; n --)
                            {
                                stack.Push(aBytes[n]);
                            }
                            
                            stack.Push(typeCode);
                            break;
                        case OpCodes.LOAD_GLOBAL:
                            addr = Advance();

                            typeCode = globalScope.typeRegisters[addr];
                            data = globalScope.dataRegisters[addr];
                            size = TypeCodes.GetByteSize(typeCode);
                            aBytes = BitConverter.GetBytes(data);
                            for (var n = size -1 ; n >= 0; n --)
                            {
                                stack.Push(aBytes[n]);
                            }
                            
                            stack.Push(typeCode);
                            break;
                        case OpCodes.CAST:
                            typeCode = Advance();
                            VmUtil.Cast(stack, typeCode);
                            break;
                        
                        case OpCodes.ALLOC:
                            // next value is an int, we know this.
                            VmUtil.ReadAsInt(stack, out var allocLength);
                            heap.Allocate(allocLength, out var allocPtr);
                            // push the address onto the stack
                            bBytes = BitConverter.GetBytes(allocPtr);
                            VmUtil.Push(stack, bBytes, TypeCodes.INT);
                            
                            break;
                        case OpCodes.DISCARD:
                            stack.Pop();
                            break;
                        case OpCodes.WRITE:
                            VmUtil.WriteToHeap(stack, heap, false);
                            break;
                        case OpCodes.WRITE_PTR:
                            VmUtil.WriteToHeap(stack, heap, true);
                            break;
                        case OpCodes.READ:
                            
                            VmUtil.ReadAsInt(stack, out var readPtr);
                            VmUtil.ReadAsInt(stack, out var readLength);
                            heap.Read(readPtr, readLength, out aBytes);
                            for (var r = readLength -1; r >= 0; r--)
                            // for (var r = 0; r < readLength; r ++)
                            {
                                var b = aBytes[r];
                                stack.Push(b);
                            }
                            
                            break;
                        case OpCodes.LENGTH:
                            VmUtil.ReadAsInt(stack, out var readLengthPtr);
                            heap.GetAllocationSize(readLengthPtr, out var readAllocLength);
                            VmUtil.Push(stack, BitConverter.GetBytes(readAllocLength), TypeCodes.INT);
                            break;
                        case OpCodes.CALL_HOST:
                            
                            VmUtil.ReadAsInt(stack, out var hostMethodPtr);
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
                
            }

        }   
    }
}
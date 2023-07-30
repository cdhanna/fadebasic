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

    
    public class VirtualMachine
    {
        private readonly StreamWriter _standardOut;
        public readonly byte[] program;

        public int instructionIndex;

        public Stack<byte> stack = new Stack<byte>();
        public VmHeap heap = new VmHeap();
        public HostMethodTable hostMethods = new HostMethodTable();
        public Stack<int> methodStack = new Stack<int>();
        
        public ulong[] dataRegisters; // parallel array with typeReg
        public byte[] typeRegisters;  // parallel array with dataReg

        
        
        public VirtualMachine(IEnumerable<byte> program, StreamWriter standardOut=null)
        {
            this.program = program.ToArray();
            if (standardOut == null)
            {
                standardOut = new StreamWriter(new MemoryStream());
            }
            
            _standardOut = standardOut;

            dataRegisters = new ulong[256];
            typeRegisters = new byte[256];
            for (var i = 0; i < dataRegisters.Length; i++)
            {
                dataRegisters[i] = 0;
                typeRegisters[i] = 0;
            }
        }

        public string ReadStdOut()
        {
            _standardOut.Flush();
            _standardOut.BaseStream.Seek(0, SeekOrigin.Begin);
            var sr = new StreamReader(_standardOut.BaseStream);
            return sr.ReadToEnd();
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

        public void Execute2(int instructionBatchCount=1000)
        {
            // while (true)
            {
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
                        // yield return new ExecutionState
                        // {
                        //     isComplete = true
                        // }; // we are done!
                        // yield break;
                    }


                    var ins = Advance();
                    // ulong a = 0, b = 0, aTypeCode = 0, bTypeCode = 0;
                    byte[] aBytes = new byte[] { };
                    byte[] bBytes = new byte[] { };
                    byte[] cBytes = new byte[] { };
                    byte aTypeCode = 0, bTypeCode = 0, vTypeCode = 0, typeCode = 0;
                    ulong data = 0;
                    byte addr = 0, size =0;
                    int insPtr;
                    switch (ins)
                    {
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
                        case OpCodes.JUMP_GTE_ZERO:
                            VmUtil.ReadAsInt(stack, out insPtr);
                            VmUtil.ReadAsInt(stack, out var jumpValue2);
                            if (jumpValue2 >= 0)
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
                        case OpCodes.ABS:
                            VmUtil.Read(stack, out typeCode, out aBytes);
                            VmUtil.Abs(typeCode, aBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, typeCode);
                            break;
                        case OpCodes.ADD:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.Add(heap, vTypeCode, aBytes, bBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, vTypeCode);
                            break;
                        case OpCodes.ADD2:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.Add(heap, vTypeCode, aBytes, bBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, vTypeCode);
                            break;
                        case OpCodes.MUL:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.Multiply(vTypeCode, aBytes, bBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, vTypeCode);
                            break;
                        case OpCodes.MUL2:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.Multiply(vTypeCode, aBytes, bBytes, out cBytes);

                            var cInt = BitConverter.ToInt32(cBytes, 0);
                            
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

                            dataRegisters[addr] = data;
                            typeRegisters[addr] = typeCode;
                            break;
                        case OpCodes.LOAD:
                            addr = Advance();

                            typeCode = typeRegisters[addr];
                            data = dataRegisters[addr];
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
                        case OpCodes.DBG_PRINT:

                            aTypeCode = stack.Pop();
                            VmUtil.Read(stack, aTypeCode, out var bytes);
                            var dbgValue = VmUtil.DbgConvert(aTypeCode, bytes);
                            _standardOut.WriteLine(aTypeCode + " - " + dbgValue);
                            break;
                        case OpCodes.NOOP:
                            // do nothing! Its a no-op!
                            break;
                        default:
                            throw new Exception("Unknown op code: " + ins);
                    }
                }

                // if there is anything left on the stack
                var remaining = stack.Count;
                // yield return new ExecutionState
                // {
                //     isComplete = false
                // };
            }

        }   
    }
}
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
                    byte aTypeCode = 0, bTypeCode = 0, vTypeCode = 0;
                    ulong data = 0;
                    byte addr = 0, size =0;
                    switch (ins)
                    {
                        case OpCodes.BPUSH:
                            var code = Advance();
                            stack.Push(code);
                            break;
                        case OpCodes.PUSH:
                            var typeCode = Advance();

                            size = TypeCodes.GetByteSize(typeCode);
                            for (var n = 0; n < size; n ++)
                            {
                                var value = Advance();
                                stack.Push(value);
                            }
                            
                            stack.Push(typeCode);
                            break;
                        
                        case OpCodes.ADD:
                            VmUtil.ReadTwoValues(stack, out vTypeCode, out aBytes, out bBytes);
                            VmUtil.Add(vTypeCode, aBytes, bBytes, out cBytes);
                            VmUtil.Push(stack, cBytes, vTypeCode);
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
                        // case OpCodes.SUB:
                        //     a = stack.Pop();
                        //     b = stack.Pop();
                        //     stack.Push(a - b);
                        //     break;
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
                            
                            VmUtil.ReadAsInt(stack, out var writePtr);
                            VmUtil.ReadAsInt(stack, out var writeLength);

                            bBytes = new byte[writeLength];
                            for (var w = 0; w < writeLength; w++)
                            {
                                var b = stack.Pop();
                                bBytes[w] = b;
                            }
                            
                            heap.Write(writePtr, writeLength, bBytes);
                            
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
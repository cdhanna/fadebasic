using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DarkBasicYo.Virtual
{

    public static class Registers
    {
        // these numbers are just meaningless... Just for util purposes...
        public const byte R0 = 0;
        public const byte R1 = 1;
        public const byte R2 = 2;
        public const byte R3 = 3;
        public const byte R4 = 4;
    }

    public static class TypeCodes
    {
        public const byte INT     = 0x00; // 4 bytes
        public const byte REAL    = 0x01; // 4 bytes
        public const byte BOOL    = 0x02; // 1 bytes
        public const byte BYTE    = 0x03; // 1 bytes
        public const byte WORD    = 0x04; // 2 bytes
        public const byte DWORD   = 0x05; // 4 bytes
        public const byte DINT    = 0x06; // 8 bytes
        public const byte DFLOAT  = 0x07; // 8 bytes
        // public const byte STRING  = 0x09; // 4 bytes

        public static readonly byte[] SIZE_TABLE = new byte[]
        {
            4, // int
            4, // real
            1, // bool
            1, // byte
            2, // word
            4, // dword
            8, // dint
            8  // dfloat
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetByteSize(byte typeCode) => SIZE_TABLE[typeCode];
    }

    public static class OpCodes
    {
        /// <summary>
        /// Pushes the next literal value
        /// </summary>
        public const byte PUSH = 1;
        
        /// <summary>
        /// Expects to find two values in the stack 
        /// </summary>
        public const byte ADD = 2;
        public const byte MUL = 3;
        public const byte DIVIDE = 4;
        public const byte SUB = 5;
        
        /// <summary>
        /// A command that prints the current value of the stack
        /// </summary>
        public const byte DBG_PRINT = 6;

        /// <summary>
        /// the next byte in the INS is the Address, and then it expects to find a value in the stack
        /// </summary>
        public const byte STORE = 7;
        
        /// <summary>
        /// the next byte in the INS is the address, and then this will push the register value onto the stack
        /// </summary>
        public const byte LOAD = 8;
        
        /// <summary>
        /// the next byte in the INS is the type code to cast the current stack value to. The stack item will be replaced with the cast value
        /// </summary>
        public const byte CAST = 9;
    }

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
                            // read a register location, which is always 2 bytes.
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
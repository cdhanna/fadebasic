using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Runtime.CompilerServices;

namespace DarkBasicYo.Virtual
{

    public static class OpCodeUtil
    {
        public const byte PUSH_TEMPLATE = 0x01;
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

        public static byte GetByteSize(byte typeCode) => SIZE_TABLE[typeCode];
    }

    public static class OpCodes
    {
        /// <summary>
        /// The next value in the bytecode is a literal value that is stored.
        /// </summary>
        public const byte PUSH = 1;
        
        public const byte ADD = 2;
        public const byte MUL = 3;
        public const byte DIVIDE = 4;
        public const byte SUB = 5;
        
        /// <summary>
        /// A command that prints the current value of the stack
        /// </summary>
        public const byte DBG_PRINT = 6;

        public const byte STORE = 7;
        public const byte LOAD = 8;
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
        

        public VirtualMachine(IEnumerable<byte> program, StreamWriter standardOut=null)
        {
            this.program = program.ToArray();
            if (standardOut == null)
            {
                standardOut = new StreamWriter(new MemoryStream());
            }
            
            _standardOut = standardOut;

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

        public IEnumerator<ExecutionState> Execute(int instructionBatchCount=1000)
        {
            while (true)
            {
                for (var i = 0; i < instructionBatchCount; i++)
                {
                    // if at end of program, exit.
                    if (instructionIndex >= program.Length)
                    {
                        if (stack.Count > 0)
                        {
                            throw new Exception("Left over stack");
                        }
                        yield return new ExecutionState
                        {
                            isComplete = true
                        }; // we are done!
                        yield break;
                    }


                    var ins = Advance();
                    // ulong a = 0, b = 0, aTypeCode = 0, bTypeCode = 0;
                    byte[] aBytes = new byte[] { };
                    byte[] bBytes = new byte[] { };
                    byte[] cBytes = new byte[] { };
                    byte aTypeCode = 0, bTypeCode = 0, vTypeCode = 0;
                    BigInteger a = 0, b = 0, x = 0;
                    switch (ins)
                    {
                        case OpCodes.PUSH:
                            var typeCode = Advance();

                            var size = TypeCodes.GetByteSize(typeCode);
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
                        // case OpCodes.SUB:
                        //     a = stack.Pop();
                        //     b = stack.Pop();
                        //     stack.Push(a - b);
                        //     break;
                        // case OpCodes.DIVIDE:
                        //     a = stack.Pop();
                        //     b = stack.Pop();
                        //     stack.Push(a / b);
                        //     break;
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
                yield return new ExecutionState
                {
                    isComplete = false
                };
            }

        }   
    }
}
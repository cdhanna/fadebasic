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
        public const byte VOID    = 0x08; // 0 bytes

        public static readonly byte[] SIZE_TABLE = new byte[]
        {
            4, // int
            4, // real
            1, // bool
            1, // byte
            2, // word
            4, // dword
            8, // dint
            8, // dfloat
            0  // void
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

        /// <summary>
        /// The length should be on the stack, and the result will be a memory address for the start of the reserved memory
        /// </summary>
        public const byte ALLOC = 10;

        /// <summary>
        /// Reads a set of bytes from the heap. Next stack value is ptr, following stack value is length. The bytes will be written to the stack
        /// </summary>
        public const byte READ = 11;

        /// <summary>
        /// Writes data to the heap. next stack should be ptr, then an int for length, then as many bytes as length provided
        /// </summary>
        public const byte WRITE = 12;

        /// <summary>
        /// Similar to PUSH, but the type code is not pushed at the end
        /// </summary>
        public const byte BPUSH = 13;

        /// <summary>
        /// Run a host function. The next value in the stack should be the address of the host function
        /// </summary>
        public const byte CALL_HOST = 14;

        /// <summary>
        /// Reads a value from the stack, and does nothing with it.
        /// </summary>
        public const byte DISCARD = 15; 
    }
}
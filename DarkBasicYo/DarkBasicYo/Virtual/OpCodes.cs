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
        public const byte INT      = 0x00; // 4 bytes
        public const byte REAL     = 0x01; // 4 bytes
        public const byte BOOL     = 0x02; // 1 bytes
        public const byte BYTE     = 0x03; // 1 bytes
        public const byte WORD     = 0x04; // 2 bytes
        public const byte DWORD    = 0x05; // 4 bytes
        public const byte DINT     = 0x06; // 8 bytes
        public const byte DFLOAT   = 0x07; // 8 bytes
        public const byte VOID     = 0x08; // 0 bytes
        public const byte STRING   = 0x09; // 4 bytes (ptr)
        public const byte PTR_REG  = 0x0A; // 1 byte (registry ptr)
        public const byte PTR_HEAP = 0x0B; // 4 bytes (heap ptr)
        public const byte STRUCT   = 0x0C; // 4 bytes (ptr)


        public const byte ANY      = 254; // this isn't a real type code, it is a fake number used for calling C# methods
        public const byte VM       = 255; // this isn't a real type code, it is used as a hack in calling C# methods

        public static readonly byte[] ORDER_PREC = new byte[]
        {
            30, // int
            50, // real
            30, // bool
            29, // byte
            31, // word
            30, // dword
            30, // dint
            60, // dfloat
            30, // void
            30, // string (int ptr)
            10, // ptr_reg
            10, // ptr_heap
            10, // struct (int ptr)
        };

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
            0, // void
            4, // string (int ptr)
            1, // ptr_reg
            4, // ptr_heap
            4, // struct (int ptr)
        };

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte GetByteSize(byte typeCode) => SIZE_TABLE[typeCode];

        public static byte GetOrder(byte typeCode) => ORDER_PREC[typeCode];
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
        public const byte BREAKPOINT = 255;
        public const byte ABS = 5;
        public const byte GT = 24;
        public const byte LT = 25;
        public const byte GTE = 26;
        public const byte LTE = 27;
        public const byte EQ = 28;
        // public const byte NEQ = 29;
        public const byte MOD = 30;
        public const byte POWER = 31;
        public const byte AND = 32;
        public const byte OR = 33;
        public const byte NOT = 34;
        
        /// <summary>
        /// A command that prints the current value of the stack
        /// </summary>
        public const byte DBG_PRINT = 100;

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
        /// Push a single byte onto the stack, from the INS 
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

        /// <summary>
        /// Reads a ptr value from the stack, and that ptr is used as a heap address. The result on the stack is the length (in bytes) of the allocation on the heap
        /// </summary>
        public const byte LENGTH = 16;

        /// <summary>
        /// Duplicates the current value on the stack, like a type-code qualified int or word
        /// </summary>
        public const byte DUPE = 17;

        /// <summary>
        /// Same as write, but pushes the original ptr back onto the stack
        /// </summary>
        public const byte WRITE_PTR = 19;

        /// <summary>
        /// Exactly the same as push, but it does not push the type code aftewards
        /// </summary>
        public const byte PUSH_TYPELESS = 18;

        /// <summary>
        /// Does nothing. Used as a place holder for label instructions.
        /// </summary>
        public const byte NOOP = 20;
        
        /// <summary>
        /// the next value on the stack is the Instruction ptr to jump to
        /// </summary>
        public const byte JUMP = 21;

        /// <summary>
        /// Similar to JUMP, but will push the existing INS ptr onto the method-stack before jumping
        /// </summary>
        public const byte JUMP_HISTORY = 22;

        /// <summary>
        /// pops a value off the method-call stack, and changes the instruction ptr to that new value
        /// </summary>
        public const byte RETURN = 23;

        /// <summary>
        /// pops an address, then pops a value. If the value is greater than zero, then the Instruction
        /// pointer is set to the address; otherwise nothing happens
        /// </summary>
        public const byte JUMP_GT_ZERO = 35;

        /// <summary>
        /// pops an address, then pops a value. If the value is equal to zero, then the Instruction
        /// pointer is set to the address; otherwise nothing happens
        /// </summary>
        public const byte JUMP_ZERO = 37;

        /// <summary>
        /// pops the two values off the stack, and puts them back in MAX/MIN order
        /// </summary>
        public const byte MIN_MAX_PUSH = 38;

        /// <summary>
        /// Next INS is the size of the jump table, not including the default case.
        /// Then, by pairs of 2, read from INS, a literal constant, and an address.
        /// Finally, read the address of the Default case
        ///
        /// Then, pop a value off the stack and treat it as the key. Jump to the address.
        /// </summary>
        public const byte JUMP_TABLE = 39;
        
        /// <summary>
        /// Creates a new scope for variables to live inside.
        /// All registers are affected
        /// </summary>
        public const byte PUSH_SCOPE = 40;

        /// <summary>
        /// Removes the current scope and pops a new scope
        /// All registers are affected
        /// </summary>
        public const byte POP_SCOPE = 41;

        /// <summary>
        /// Just go kaboom
        /// </summary>
        public const byte EXPLODE = 42;

        /// <summary>
        /// Exactly the same as <see cref="LOAD"/>, but always pulls from the global scope
        /// </summary>
        public const byte LOAD_GLOBAL = 43;
        
        /// <summary>
        /// Exactly the same as <see cref="STORE"/>, but always pulls from the global scope
        /// </summary>
        public const byte STORE_GLOBAL = 44;

    }
}
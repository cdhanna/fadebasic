using System;
using System.Collections.Generic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace FadeBasic.Sdk
{
    public static class Fade
    {
        
        public static bool TryCreate(
            string src, 
            CommandCollection commands, 
            out FadeRuntimeContext context,
            out FadeErrors errors)
        {
            return FadeRuntimeContext.TryFromSource(src, commands, out context, out errors);
        }
        
        public static bool TryRun(
            string src, 
            CommandCollection commands, 
            out FadeRuntimeContext context,
            out FadeErrors errors)
        {
            if (!FadeRuntimeContext.TryFromSource(src, commands, out context, out errors))
            {
                return false;
            }

            context.Run();
            return true;
        }
        
        public static bool TryRun(
            string src, 
            CommandCollection commands, 
            out FadeErrors errors)
        {
            if (!FadeRuntimeContext.TryFromSource(src, commands, out var ctx, out errors))
            {
                return false;
            }
            
            ctx.Run();
            return true;
        }
    }

    public class FadeErrors
    {
        public List<LexerError> LexicalErrors = new List<LexerError>();
        public List<ParseError> ParserErrors = new List<ParseError>();
    }

    public abstract class FadeArray<T> : FadeArray<T>.IArrayController
    {
        public interface IArrayController
        {
            public void SetElements(T[] elements);
            public T ReadMemoryValue(ref VmAllocation allocation, byte[] memory, int offset);
            void SetArrayMetadata(int[] dimensions, int[] strides, int elementByteSize);
        }
        public abstract byte TypeCode { get; }
        public int Ranks => _dimensions.Length;

        public int[] Dimensions => _dimensions;
        
        private int[] _dimensions;
        private int[] _strides;
        private int _elementByteSize;

        private T[] rawElements;

        public T GetElement(params int[] indecies)
        {
            if (indecies.Length != Ranks)
            {
                throw new InvalidOperationException(
                    $"Array has {Ranks} dimensions, so cannot be indexed with {indecies.Length} values");
            }

            var index = 0;
            for (var i = 0; i < indecies.Length; i++)
            {
                index += indecies[i] * _strides[i];
            }

            return rawElements[index];
        }

        public T GetRawElement(int index)
        {
            return rawElements[index];
        }

        void IArrayController.SetElements(T[] elements)
        {
            rawElements = elements;
        }

        void IArrayController.SetArrayMetadata(int[] dimensions, int[] strides, int elementByteSize)
        {
            _dimensions = dimensions;
            _strides = strides;
            _elementByteSize = elementByteSize;
        }

        T IArrayController.ReadMemoryValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return ReadValue(ref allocation, memory, offset);
        }
        
        protected abstract T ReadValue(ref VmAllocation allocation, byte[] memory, int offset);
    }
    
    public class FadeIntegerArray : FadeArray<int>
    {
        public override byte TypeCode => TypeCodes.INT;
        protected override int ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToInt32(memory, offset);
        }
    }
    
    
    public class FadeDoubleIntegerArray : FadeArray<long>
    {
        public override byte TypeCode => TypeCodes.DINT;
        protected override long ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToInt64(memory, offset);
        }
    }
    
    public class FadeWordArray : FadeArray<ushort>
    {
        public override byte TypeCode => TypeCodes.WORD;
        protected override ushort ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToUInt16(memory, offset);
        }
    }
    
    public class FadeDWordArray : FadeArray<uint>
    {
        public override byte TypeCode => TypeCodes.DWORD;
        protected override uint ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToUInt32(memory, offset);
        }
    }
    
    public class FadeByteArray : FadeArray<byte>
    {
        public override byte TypeCode => TypeCodes.BYTE;
        protected override byte ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return memory[offset];
        }
    }
    
    public class FadeBoolArray : FadeArray<bool>
    {
        public override byte TypeCode => TypeCodes.BOOL;
        protected override bool ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return memory[offset] > 0;
        }
    }
    
    public class FadeFloatArray : FadeArray<float>
    {
        public override byte TypeCode => TypeCodes.REAL;
        protected override float ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToSingle(memory, offset);
        }
    }
    
    
    public class FadeDoubleFloatArray : FadeArray<double>
    {
        public override byte TypeCode => TypeCodes.DFLOAT;
        protected override double ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            return BitConverter.ToDouble(memory, offset);
        }
    }

    public class FadeObjectArray : FadeArray<FadeObject>
    {
        private readonly FadeRuntimeContext _ctx;
        public override byte TypeCode => TypeCodes.STRUCT;

        public FadeObjectArray(FadeRuntimeContext ctx)
        {
            _ctx = ctx;
          
        }
        
        protected override FadeObject ReadValue(ref VmAllocation allocation, byte[] memory, int offset)
        {
            _ctx.ReadSubObject(ref allocation, allocation.format.typeId, memory, offset, out var obj);
            return obj;
        }
    }
    
    public class FadeObject
    {
        public string typeName;

        public Dictionary<string, FadeObject> objects = new Dictionary<string, FadeObject>();
        public Dictionary<string, bool> boolFields = new Dictionary<string, bool>();
        public Dictionary<string, byte> byteFields = new Dictionary<string, byte>();
        public Dictionary<string, ushort> wordFields = new Dictionary<string, ushort>();
        public Dictionary<string, uint> dWordFields = new Dictionary<string, uint>();
        public Dictionary<string, int> integerFields = new Dictionary<string, int>();
        public Dictionary<string, long> doubleIntegerFields = new Dictionary<string, long>();
        public Dictionary<string, float> floatFields = new Dictionary<string, float>();
        public Dictionary<string, double> doubleFloatFields = new Dictionary<string, double>();
        public Dictionary<string, string> stringFields = new Dictionary<string, string>();

        // public Dictionary<string, FadeValue> fields = new Dictionary<string, FadeValue>();
    }

    public class FadeValue
    {
        public ulong rawData;
        public byte typeCode;
    }

    public class FadeRuntimeContext
    {
        private static Lexer _lexer = new Lexer();

        public VirtualMachine Machine { get; private set; }
        public Parser Parser { get; private set; }
        public ProgramNode Program { get; private set; }
        public Compiler Compiler { get; private set; }

        public bool DidProgramComplete => Machine.instructionIndex >= Machine.program.Length;
        
        private FadeRuntimeContext()
        {

        }

        public void Run()
        {
            Machine.Execute2();
        }

        public void RunPartial(int opCodeBudget)
        {
            Machine.Execute2(opCodeBudget);
        }

        public void Suspend()
        {
            Machine.Suspend();
        }

        public bool TryGetInteger(string name, out int value)
        {
            return TryGetInteger(name, out value, out _);
        }

        public bool TryGetInteger(string name, out int value, out string error)
        {
            value = default;
            error = null;
            if (!TryFindVariable(name, out _, out var scope, out var index))
            {
                error = "could not find variable";
                return false;
            }

            if (scope.typeRegisters[index] != TypeCodes.INT)
            {
                error = "variable is not an integer";
                return false;
            }

            value = VmUtil.ConvertToInt(scope.dataRegisters[index]);
            return true;
        }
        
        
        public bool TryGetDoubleInteger(string name, out long value)
        {
            return TryGetDoubleInteger(name, out value, out _);
        }

        public bool TryGetDoubleInteger(string name, out long value, out string error)
        {
            value = default;
            error = null;
            if (!TryFindVariable(name, out _, out var scope, out var index))
            {
                error = "could not find variable";
                return false;
            }

            if (scope.typeRegisters[index] != TypeCodes.DINT)
            {
                error = "variable is not a double integer";
                return false;
            }

            value = VmUtil.ConvertToDInt(scope.dataRegisters[index]);
            return true;
        }
        
        
        public bool TryGetWord(string name, out ushort value)
        {
            return TryGetWord(name, out value, out _);
        }

        public bool TryGetWord(string name, out ushort value, out string error)
        {
            value = default;
            error = null;
            if (!TryFindVariable(name, out _, out var scope, out var index))
            {
                error = "could not find variable";
                return false;
            }

            if (scope.typeRegisters[index] != TypeCodes.WORD)
            {
                error = "variable is not a word";
                return false;
            }

            value = VmUtil.ConvertToWord(scope.dataRegisters[index]);
            return true;
        }
        
        
        public bool TryGetDWord(string name, out uint value)
        {
            return TryGetDWord(name, out value, out _);
        }

        public bool TryGetDWord(string name, out uint value, out string error)
        {
            value = default;
            error = null;
            if (!TryFindVariable(name, out _, out var scope, out var index))
            {
                error = "could not find variable";
                return false;
            }

            if (scope.typeRegisters[index] != TypeCodes.DWORD)
            {
                error = "variable is not a dword";
                return false;
            }

            value = VmUtil.ConvertToDWord(scope.dataRegisters[index]);
            return true;
        }
        
        
        public bool TryGetDoubleFloat(string name, out double value)
        {
            return TryGetDoubleFloat(name, out value, out _);
        }

        public bool TryGetDoubleFloat(string name, out double value, out string error)
        {
            value = default;
            error = null;
            if (!TryFindVariable(name, out _, out var scope, out var index))
            {
                error = "could not find variable";
                return false;
            }

            if (scope.typeRegisters[index] != TypeCodes.DFLOAT)
            {
                error = "variable is not a double float";
                return false;
            }

            value = VmUtil.ConvertToDFloat(scope.dataRegisters[index]);
            return true;
        }
        
        public bool TryGetFloat(string name, out float value)
        {
            return TryGetFloat(name, out value, out _);
        }

        public bool TryGetFloat(string name, out float value, out string error)
        {
            value = default;
            error = null;
            if (!TryFindVariable(name, out _, out var scope, out var index))
            {
                error = "could not find variable";
                return false;
            }

            if (scope.typeRegisters[index] != TypeCodes.REAL)
            {
                error = "variable is not a float";
                return false;
            }

            value = VmUtil.ConvertToFloat(scope.dataRegisters[index]);
            return true;
        }
        
        public bool TryGetByte(string name, out byte value)
        {
            return TryGetByte(name, out value, out _);
        }

        public bool TryGetByte(string name, out byte value, out string error)
        {
            value = default;
            error = null;
            if (!TryFindVariable(name, out _, out var scope, out var index))
            {
                error = "could not find variable";
                return false;
            }

            if (scope.typeRegisters[index] != TypeCodes.BYTE)
            {
                error = "variable is not a byte";
                return false;
            }

            value = VmUtil.ConvertToByte(scope.dataRegisters[index]);
            return true;
        }
        
        
        public bool TryGetBool(string name, out bool value)
        {
            return TryGetBool(name, out value, out _);
        }

        public bool TryGetBool(string name, out bool value, out string error)
        {
            value = default;
            error = null;
            if (!TryFindVariable(name, out _, out var scope, out var index))
            {
                error = "could not find variable";
                return false;
            }

            if (scope.typeRegisters[index] != TypeCodes.BOOL)
            {
                error = "variable is not a bool";
                return false;
            }

            value = VmUtil.ConvertToByte(scope.dataRegisters[index]) > 0;
            return true;
        }
        
        
        public bool TryGetString(string name, out string value)
        {
            return TryGetString(name, out value, out _);
        }

        public bool TryGetString(string name, out string value, out string error)
        {
            value = default;
            error = null;
            if (!TryFindVariable(name, out _, out var scope, out var index))
            {
                error = "could not find variable";
                return false;
            }

            if (scope.typeRegisters[index] != TypeCodes.STRING)
            {
                error = "variable is not a string";
                return false;
            }

            var address = VmUtil.ConvertToInt(scope.dataRegisters[index]);
            return TryReadString(address, out value, out error);
        }

        bool TryReadString(int address, out string value, out string error)
        {
            value = null;
            error = null;
            if (Machine.heap.TryGetAllocationSize(address, out var strSize))
            {
                Machine.heap.Read(address, strSize, out var strBytes);
                value = VmConverter.ToString(strBytes);
                return true;
            }
            else
            {
                error = "invalid memory address";
                return false;
            }
        }

        public bool TryGetObjectArray(string name, out FadeObjectArray value, out string error)
        {
            FadeArray<FadeObject> array = value = new FadeObjectArray(this);
            return TryGetArray(name, ref array, out error);
        }
        public bool TryGetIntegerArray(string name, out FadeIntegerArray value, out string error)
        {
            FadeArray<int> array = value = new FadeIntegerArray();
            return TryGetArray(name, ref array, out error);
        }
        public bool TryGetDoubleIntegerArray(string name, out FadeDoubleIntegerArray value, out string error)
        {
            FadeArray<long> array = value = new FadeDoubleIntegerArray();
            return TryGetArray(name, ref array, out error);
        }
        public bool TryGetWordArray(string name, out FadeWordArray value, out string error)
        {
            FadeArray<ushort> array = value = new FadeWordArray();
            return TryGetArray(name, ref array, out error);
        }
        public bool TryGetDWordArray(string name, out FadeDWordArray value, out string error)
        {
            FadeArray<uint> array = value = new FadeDWordArray();
            return TryGetArray(name, ref array, out error);
        }
        public bool TryGetByteArray(string name, out FadeByteArray value, out string error)
        {
            FadeArray<byte> array = value = new FadeByteArray();
            return TryGetArray(name, ref array, out error);
        }
        public bool TryGetBoolArray(string name, out FadeBoolArray value, out string error)
        {
            FadeArray<bool> array = value = new FadeBoolArray();
            return TryGetArray(name, ref array, out error);
        }
        public bool TryGetFloatArray(string name, out FadeFloatArray value, out string error)
        {
            FadeArray<float> array = value = new FadeFloatArray();
            return TryGetArray(name, ref array, out error);
        }
        public bool TryGetDoubleFloatArray(string name, out FadeDoubleFloatArray value, out string error)
        {
            FadeArray<double> array = value = new FadeDoubleFloatArray();
            return TryGetArray(name, ref array, out error);
        }
        private bool TryGetArray<T>(string name, ref FadeArray<T> value, out string error)
        {
            error = null;
            
            if (!TryFindVariable(name, out _, out var scope, out var index))
            {
                error = "could not find variable";
                return false;
            }
            
          
            var address = VmUtil.ConvertToInt(scope.dataRegisters[index]);
            if (!Machine.heap.TryGetAllocation(address, out var allocation))
            {
                error = "invalid memory address";
                return false;
            }

            if (!allocation.format.IsArray(out var ranks))
            {
                error = "invalid memory address, not an array pointer";
                return false;
            }
            
            Machine.heap.Read(address, allocation.length, out var bytes);

            var typeCode = allocation.format.typeCode;
            var elemSize = (int)TypeCodes.GetByteSize(typeCode);
            CompiledType type = null;
            if (typeCode == TypeCodes.STRUCT)
            {
                type = Compiler._typeTable[allocation.format.typeId];
                elemSize = type.byteSize;
            }
            
            if (allocation.format.typeCode != value.TypeCode)
            {
                error = "variable is not correct element type";
                return false;
            }

            var valueControl = value as FadeArray<T>.IArrayController;
            
            var dimensions = new int[ranks];
            var strides = new int[ranks];
            var elementByteSize = elemSize;
            var totalElements = 1;
            
            for (var rank = 0; rank < ranks; rank++)
            {
                var elementStrideAddr = index + ranks * 2 - (rank * 2 );
                var elementSizeRegAddr = elementStrideAddr - 1;

                var rankSize = (int) scope.dataRegisters[elementSizeRegAddr]; // TODO invalid cast? 
                var rankStride = (int) scope.dataRegisters[elementStrideAddr];

                totalElements *= rankSize ;
                dimensions[rank] = rankSize;
                strides[rank] = rankStride;
            }

            var rawElements = new T[totalElements];
            valueControl.SetArrayMetadata(dimensions, strides, elementByteSize);
            for (var i = 0; i < totalElements; i++)
            {
                rawElements[i] = valueControl.ReadMemoryValue(ref allocation, bytes, elemSize * i);
            }
            valueControl.SetElements(rawElements);
            
            return true;
        }
        

        public bool TryGetObject(string name, out FadeObject value, out string error)
        {
            value = default;
            error = null;
            if (!TryFindVariable(name, out _, out var scope, out var index))
            {
                error = "could not find variable";
                return false;
            }
            
            if (scope.typeRegisters[index] != TypeCodes.STRUCT)
            {
                error = "variable is not a object";
                return false;
            }
            
            var address = VmUtil.ConvertToInt(scope.dataRegisters[index]);
            return TryReadObject(address, out value, out error);
            
        }
        
        

        bool TryReadObject(int address, out FadeObject value, out string error)
        {
            value = null;
            error = null;
            if (Machine.heap.TryGetAllocation(address, out var allocation))
            {
                Machine.heap.Read(address, allocation.length, out var bytes);

                ReadSubObject(ref allocation, allocation.format.typeId, bytes, 0, out value);
          
                return true;
            }
            else
            {
                error = "invalid memory address";
                return false;
            }
        }
        
        public void ReadSubObject(ref VmAllocation allocation, int typeId, byte[] bytes, int offset, out FadeObject value)
        {
            value = null;
            var type = Compiler._typeTable[typeId];
            value = new FadeObject
            {
                typeName = type.typeName,
            };
            foreach (var field in type.fields)
            {
                var fieldName = field.Key;
                var member = field.Value;
                ReadMember(ref allocation, offset, value, fieldName, ref member, bytes);
            }
        }

        void ReadMember(ref VmAllocation allocation, int offset, FadeObject value, string fieldName, ref CompiledTypeMember member, byte[] memory)
        {
            switch (member.TypeCode)
            {
                case TypeCodes.BYTE:
                    value.byteFields[fieldName] = memory[member.Offset + offset];
                    break;
                case TypeCodes.BOOL:
                    value.boolFields[fieldName] = memory[member.Offset + offset] > 0;
                    break;
                case TypeCodes.WORD:
                    value.wordFields[fieldName] = BitConverter.ToUInt16(memory, member.Offset + offset);
                    break;
                case TypeCodes.DWORD:
                    value.dWordFields[fieldName] = BitConverter.ToUInt32(memory, member.Offset + offset);
                    break;
                case TypeCodes.INT:
                    value.integerFields[fieldName] = BitConverter.ToInt32(memory, member.Offset + offset);
                    break;
                case TypeCodes.DINT:
                    value.doubleIntegerFields[fieldName] = BitConverter.ToInt64(memory, member.Offset + offset);
                    break;
                case TypeCodes.REAL:
                    value.floatFields[fieldName] = BitConverter.ToSingle(memory, member.Offset + offset);
                    break;
                case TypeCodes.DFLOAT:
                    value.doubleFloatFields[fieldName] = BitConverter.ToDouble(memory, member.Offset + offset);
                    break;
                case TypeCodes.STRING:
                    var strPtr = BitConverter.ToInt32(memory, member.Offset + offset);
                    TryReadString(strPtr, out var strValue, out _);
                    value.stringFields[fieldName] = strValue;
                    break;
                case TypeCodes.STRUCT:
                    ReadSubObject(ref allocation, member.Type.typeId, memory, member.Offset + offset, out var subObj);
                    value.objects[fieldName] = subObj;
                    break;
            }
            
        }

        private bool TryFindVariable(string name, out DebugVariable variable, out VirtualScope scope, out int index)
        {
            variable = null;
            scope = Machine.scope;
            for (index = 0; index < Machine.scope.insIndexes.Length; index++)
            {
                var insIndex = Machine.scope.insIndexes[index];
                if (!Compiler.DebugData.insToVariable.TryGetValue(insIndex, out var v))
                {
                    continue;
                }

                if (v.name == name)
                {
                    variable = v;
                    return true;
                }
            }

            return false;
        }

        public static bool TryFromSource(string src, 
            CommandCollection commands, 
            out FadeRuntimeContext context,
            out FadeErrors errors)
        {
            errors = null;
            context = null;

            errors = null;

            var lexResults = _lexer.TokenizeWithErrors(src, commands);
            if (lexResults.tokenErrors.Count > 0)
            {
                errors = new FadeErrors
                {
                    LexicalErrors = lexResults.tokenErrors
                };
                return false;
            }

            var parser = new Parser(lexResults.stream, commands);
            var program = parser.ParseProgram();

            var parseErrors = program.GetAllErrors();
            if (parseErrors.Count > 0)
            {
                errors = new FadeErrors
                {
                    ParserErrors = parseErrors
                };
                return false;
            }

            var compiler = new Compiler(commands, new CompilerOptions
            {
                // the SDK will always generate debug data, to enable
                //  the ability to read variables
                GenerateDebugData = true,
                InternStrings = true
            });
            compiler.Compile(program);

            var vm = new VirtualMachine(compiler.Program);
            vm.hostMethods = compiler.methodTable;

            context = new FadeRuntimeContext
            {
                Machine = vm, 
                Parser = parser, 
                Compiler = compiler,
                Program = program
            };

            return true;
        }
    }

}
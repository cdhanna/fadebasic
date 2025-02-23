using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using FadeBasic.Ast;
using FadeBasic.Launch;
using FadeBasic.Virtual;
using DebugVariable = FadeBasic.Virtual.DebugVariable;

namespace FadeBasic.Sdk
{
    public static class Fade
    {
        private static Regex _sourceRegex = new Regex(@"(?<!<!--\s*)<FadeSource\s+Include\s*=\s*""([^""]+)""\s*/?>");
        private static Regex _commandRegex = new Regex(@"(?<!<!--\s*)<FadeCommand\s+(?=[^>]*Include\s*=\s*""([^""]+)"")(?=[^>]*FullName\s*=\s*""([^""]+)"")[^>]*>");

        public static bool TryCreateFromProject(
            string csProjPath, 
            CommandCollection availableCommands, 
            out FadeRuntimeContext context, 
            out FadeErrors errors)
        {
            context = null;
            errors = null;

            var commandLookup = availableCommands.Sources.ToDictionary(s => s.CommandGroupName);

            var csProjDir = Path.GetDirectoryName(csProjPath);
            if (!File.Exists(csProjPath))
            {
                errors = new FadeErrors
                {
                    SystemErrors = new List<string>
                    {
                        "no csproj file found"
                    }
                };
            }

            var csProjText = File.ReadAllText(csProjPath);
                // <FadeSource Include="music.fbasic" />


            var fullSourcePaths = new List<string>();
            var sourceMatches = _sourceRegex.Matches(csProjText);
            for (var i = 0; i < sourceMatches.Count; i++)
            {
                var sourceMatch = sourceMatches[i];
                var relativeSourceFile = sourceMatch.Groups[1].Value;

                var fullSource = Path.Combine(csProjDir, relativeSourceFile);
                fullSourcePaths.Add(fullSource);
            }


            var usedMethodSources = new List<IMethodSource>();
            var commandMatches = _commandRegex.Matches(csProjText);
            for (var i = 0; i < commandMatches.Count; i++)
            {
                var commandMatch = commandMatches[i];
                var include = commandMatch.Groups[1].Value;
                var command = commandMatch.Groups[2].Value;

                if (!commandLookup.TryGetValue(command, out var methodSource))
                {
                    errors = new FadeErrors
                    {
                        SystemErrors = new List<string>
                        {
                            $"csproj requires command group=[{command}], but that command group was not provided."
                        }
                    };
                    return false;
                }
                usedMethodSources.Add(methodSource);
            }

            var commandCollection = new CommandCollection(usedMethodSources.ToArray());
            var sourceMap = SourceMap.CreateSourceMap(fullSourcePaths);

            return TryCreateFromString(sourceMap.fullSource, commandCollection, out context, out errors);

        }
        
        public static bool TryCreateFromString(
            string src, 
            CommandCollection commands, 
            out FadeRuntimeContext context,
            out FadeErrors errors)
        {
            return FadeRuntimeContext.TryFromSource(src, commands, out context, out errors);
        }
        
       
    }

    public class FadeErrors
    {
        public bool AnyErrors => SystemErrors.Count > 0 || LexicalErrors.Count > 0 || ParserErrors.Count > 0;
        
        public List<string> SystemErrors = new List<string>();
        public List<LexerError> LexicalErrors = new List<LexerError>();
        public List<ParseError> ParserErrors = new List<ParseError>();
    }

    public class FadeRuntimeContext
    {
        private DebugSession _session;
        private static Lexer _lexer = new Lexer();

        public VirtualMachine Machine { get; private set; }
        public Parser Parser { get; private set; }
        public ProgramNode Program { get; private set; }
        public Compiler Compiler { get; private set; }
        public CommandCollection CommandCollection { get; private set; }

        public bool DidProgramComplete => Machine.instructionIndex >= Machine.program.Length;
        
        private FadeRuntimeContext()
        {

        }

        public void Debug(bool waitForConnection=true, int port = 0, string debugLogPath=null)
        {
            if (DidProgramComplete)
                throw new Exception(
                    $"the program has exited. Use {nameof(DidProgramComplete)} to precheck before calling this method.");
            if (port <= 0)
            {
                port = LaunchUtil.FreeTcpPort();

            }
            _session = new DebugSession(Machine, Compiler.DebugData, CommandCollection, new LaunchOptions
            {
                debugWaitForConnection = waitForConnection,
                debugPort = port,
                debug = true,
                debugLogPath = debugLogPath
            }, label: "fade");

            _session.StartServer();
            _session.StartDebugging(); // infinite budget
            _session.ShutdownServer();
        }
        
        public void DebugPartial(bool waitForConnection=true, int port = 0, string debugLogPath=null)
        {
            if (DidProgramComplete)
                throw new Exception(
                    $"the program has exited. Use {nameof(DidProgramComplete)} to precheck before calling this method.");
            if (_session == null)
            {
                if (port <= 0)
                {
                    port = LaunchUtil.FreeTcpPort();
                }

                _session = new DebugSession(Machine, Compiler.DebugData, CommandCollection, new LaunchOptions
                {
                    debugWaitForConnection = waitForConnection,
                    debugPort = port,
                    debug = true,
                    debugLogPath = debugLogPath
                }, label: "fade");

                _session.StartServer();
            }

            _session.StartDebugging(1);

            if (DidProgramComplete)
            {
                _session.ShutdownServer();
            }
        }


        public void Run()
        {
            if (DidProgramComplete)
                throw new Exception(
                    $"the program has exited. Use {nameof(DidProgramComplete)} to precheck before calling this method.");
            Machine.Execute2();
        }

        public void RunPartial()
        {
            if (DidProgramComplete)
                throw new Exception(
                    $"the program has exited. Use {nameof(DidProgramComplete)} to precheck before calling this method.");
            Machine.Execute2(1);
        }

        public void Suspend()
        {
            Machine.Suspend();
        }

        public void Reset()
        {
            if (_session != null)
            {
                _session.ShutdownServer();
                _session = null;
            }
            
            Machine = new VirtualMachine(Compiler.Program);
            Machine.hostMethods = Compiler.methodTable;
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
                Program = program,
                CommandCollection = commands
            };

            return true;
        }
    }

}
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
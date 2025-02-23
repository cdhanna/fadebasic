using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Ast.Visitors;
using FadeBasic.Sdk;
using FadeBasic.Virtual;

namespace ApplicationSupport.Code;

public static class CodeUtil
{
    public static Compiler Compile(this ProgramNode programNode, CommandCollection commandCollection, CompilerOptions options)
    {
        var compiler = new Compiler(commandCollection, options);
        compiler.Compile(programNode);
        return compiler;
    }

    public static CodeUnit Parse(this SourceMap sourceMap, CommandCollection commandCollection, ParseOptions options=null)
    {
        var lexer = new Lexer();
        var unit = new CodeUnit
        {
            sourceMap = sourceMap
        };

        var source = sourceMap.fullSource;
        unit.lexerResults = lexer.TokenizeWithErrors(source, commandCollection);

        sourceMap.ProvideTokens(unit.lexerResults);
        if (unit.lexerResults.tokenErrors.Count == 0)
        {
            var parser = new Parser(unit.lexerResults.stream, commandCollection);
            unit.program = parser.ParseProgram(options);
            unit.program.AddTrivia(unit.lexerResults);
        }

        return unit;
    }
    
}
using System.Text;
using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace ApplicationSupport.Code;

public static class CodeUtil
{
    public static byte[] Compile(this ProgramNode programNode, CommandCollection commandCollection)
    {
        var compiler = new Compiler(commandCollection);
        compiler.Compile(programNode);
        return compiler.Program.ToArray();
    }

    public static CodeUnit Parse(this SourceMap sourceMap, CommandCollection commandCollection)
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
            unit.program = parser.ParseProgram();
        }

        return unit;
    }
    
}
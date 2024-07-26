using System.Text;
using DarkBasicYo;
using DarkBasicYo.ApplicationSupport.Project;
using DarkBasicYo.Ast;
using DarkBasicYo.Virtual;

namespace ApplicationSupport.Code;

public static class CodeUtil
{
    public static byte[] Compile(this ProgramNode programNode, CommandCollection commandCollection)
    {
        var compiler = new Compiler(commandCollection);
        compiler.Compile(programNode);
        return compiler.Program.ToArray();
    }

    public static string CombineSource(this ProjectContext context)
    {
        var srcBuilder = new StringBuilder();
        foreach (var path in context.absoluteSourceFiles)
        {
            var src = File.ReadAllText(path);
            srcBuilder.Append(src);
            // TODO: how to map errors back to file?
        }

        var fullSrc = srcBuilder.ToString();
        return fullSrc;
    }
    
    public static ProgramNode Parse(this string source, CommandCollection commandCollection)
    {

        var lexer = new Lexer();
        var lexerResults = lexer.TokenizeWithErrors(source, commandCollection);
        if (lexerResults.tokenErrors.Count > 0)
        {
            throw new Exception("unable to lex");
        }

        var stream = lexerResults.stream;
        var parser = new Parser(stream, commandCollection);
        var program = parser.ParseProgram();

        return program;
    }
}
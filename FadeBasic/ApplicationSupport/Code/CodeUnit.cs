using FadeBasic;
using FadeBasic.ApplicationSupport.Project;
using FadeBasic.Ast;

namespace ApplicationSupport.Code;

public class CodeUnit
{
    public LexerResults lexerResults;
    public ProgramNode program;
    public SourceMap sourceMap;
}
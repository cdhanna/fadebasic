using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Sdk;

namespace ApplicationSupport.Code;

public class CodeUnit
{
    public LexerResults lexerResults;
    public ProgramNode program;
    public ProgramNode macroProgram => lexerResults.macroProgram;
    public SourceMap sourceMap;
}
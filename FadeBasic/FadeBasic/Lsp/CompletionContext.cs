using System.Collections.Generic;
using FadeBasic.Ast;
using FadeBasic.Virtual;

namespace FadeBasic.Lsp
{
    public class CompletionContext
    {
        public CommandCollection Commands;
        public ProgramNode Program;
        public Token FakeToken;
        public Token LeftToken;
        public List<IAstVisitable> Group;
        public SymbolTable LocalScope;
        public Dictionary<string, string> ConstantTable;
        public string FunctionName;
        public bool IsMacro;

        public Scope Scope => Program.scope;
    }
}

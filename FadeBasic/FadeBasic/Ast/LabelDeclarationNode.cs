using System.Collections.Generic;

namespace FadeBasic.Ast
{
    public class LabelDefinition
    {
        public LabelDeclarationNode node;
        public int statementIndex;
    }
    
    public class LabelDeclarationNode : AstNode, IStatementNode, IHasTriviaNode
    {
        public string label;

        // public LabelDeclarationNode(Token token) : base(token, token)
        // {
        //     label = token.raw.Substring(0, token.raw.Length - 1);
        // }
        public LabelDeclarationNode(Token start, Token end)
        {
            startToken = start;
            endToken = end;
            label = start.caseInsensitiveRaw;
            
        }
        protected override string GetString()
        {
            return $"label {label}";
        }

        public override IEnumerable<IAstVisitable> IterateChildNodes()
        {
            yield break;
        }

        public string Trivia { get; set; }
    }
}
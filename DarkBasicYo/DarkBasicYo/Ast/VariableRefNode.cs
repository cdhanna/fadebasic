using System;

namespace DarkBasicYo.Ast
{

    public interface IVariableNode : IExpressionNode
    {

    }

    public class VariableRefNode : AstNode, IVariableNode
    {
        public string variableName;

        public VariableRefNode(Token token) : base(token)
        {
            variableName = token.raw;
        }

        public VariableType DefaultTypeByName
        {
            get
            {
                switch (base.startToken.type)
                {
                    case LexemType.VariableReal:
                        return VariableType.Float;
                    case LexemType.VariableGeneral:
                        return VariableType.Integer;
                    case LexemType.VariableString:
                        return VariableType.String;
                    default:
                        throw new Exception("Unknown variable type");
                }
            }
        }

        protected override string GetString()
        {
            return $"ref {variableName}";
        }
    }
}
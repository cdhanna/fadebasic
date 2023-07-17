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

        protected override string GetString()
        {
            return variableName;
        }
    }
}
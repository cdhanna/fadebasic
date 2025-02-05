using System;
using System.Text;

namespace FadeBasic.Ast.Visitors
{
    public class FormatVisitor
    {
        public static string Format(ProgramNode program)
        {
            var sb = new StringBuilder();

            foreach (var statement in program.statements)
            {
                Format(sb, statement);
            }
            
            return sb.ToString();
        }

        static void Format(StringBuilder sb, IStatementNode statement)
        {
            switch (statement)
            {
                case AssignmentStatement assignemnt:
                    Format(sb, assignemnt.variable);
                    sb.Append(" = ");
                    Format(sb, assignemnt.expression);
                    sb.AppendLine();
                    break;
                default: throw new NotImplementedException();
            }
        }

        static void Format(StringBuilder sb, IVariableNode variable)
        {
            switch (variable)
            {
                case VariableRefNode variableRef:
                    sb.Append(variableRef.variableName);
                    break;
                default: throw new NotImplementedException();
            }
        }
        
        static void Format(StringBuilder sb, IExpressionNode expression)
        {
            switch (expression)
            {
                case LiteralIntExpression literalInt:
                    sb.Append(literalInt.value);
                    break;
                default: throw new NotImplementedException();

            }
        }
    }
}
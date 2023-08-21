using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq.Expressions;
using DarkBasicYo.Ast;

namespace DarkBasicYo
{
    public class AstVisitor<T>
    {
        public virtual T Visit(ProgramNode program)
        {
            foreach (var statement in program.statements)
            {
                Visit(statement);
            }

            return default;
        }

        public virtual T Visit(IStatementNode statementNode)
        {
            switch (statementNode)
            {
                case AssignmentStatement assignment:
                    return Visit(assignment);
                case DeclarationStatement declaration:
                    return Visit(declaration);
                case WhileStatement whileStatement:
                    return Visit(whileStatement);
                case CommandStatement commandStatement:
                    return Visit(commandStatement);
                default:
                    throw new Exception("Unknown statement for visit");
            }
        }

        public virtual T Visit(AssignmentStatement assignmentStatement)
        {
            Visit(assignmentStatement.variable);
            Visit(assignmentStatement.expression);
            return default;
        }

        public virtual T Visit(CommandStatement commandStatement)
        {
            foreach (var expression in commandStatement.args)
            {
                Visit(expression);
            }

            return default;
        }

        public virtual T Visit(WhileStatement whileStatement)
        {
            Visit(whileStatement.condition);
            foreach (var statement in whileStatement.statements)
            {
                Visit(statement);
            }

            return default;
        }

        public virtual T Visit(DeclarationStatement declarationStatement)
        {
            Visit(declarationStatement.type);
            return default;
        }

        public virtual T Visit(ITypeReferenceNode typeReferenceNode)
        {
            return default;
        }

        public virtual T Visit(IExpressionNode expressionNode)
        {
            switch (expressionNode)
            {
                case VariableRefNode variable:
                    return Visit(variable);
                case BinaryOperandExpression binaryOp:
                    return Visit(binaryOp);
                case LiteralIntExpression literalInt:
                    return Visit(literalInt);
                default:
                    throw new Exception("Unknown expression type");
            }
        }

        public virtual T Visit(VariableRefNode variableNode)
        {
            return default;
        }

        public virtual T Visit(BinaryOperandExpression binaryOperation)
        {
            Visit(binaryOperation.lhs);
            Visit(binaryOperation.rhs);
            return default;
        }

        public virtual T Visit(LiteralIntExpression literalInt)
        {
            return default;
        }
    }
}
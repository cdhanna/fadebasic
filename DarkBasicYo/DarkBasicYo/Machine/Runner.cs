using System;
using System.Collections;
using System.Collections.Generic;
using DarkBasicYo.Ast;

namespace DarkBasicYo.Machine
{
    public class Runner
    {
        // public ProgramExecutionContext RunProgram(ProgramNode program)
        // {
        //     
        // }
    }

    public interface IExecutionEvent
    {
         
    }

    public class ExecutionException : Exception, IExecutionEvent
    {
        public IAstNode ProgramSource { get; }

        public ExecutionException(string message, IAstNode programSource)
            : base($"Runtime error: {message}")
        {
            ProgramSource = programSource;
        }
    }
    
    public class ProgramExecutionContext
    {
        private readonly ProgramNode _program;
        private readonly CommandCollection _commands;
        private readonly Stack<VariableCollection> _variableScopes;
        private VariableCollection _globalScope;

        public ProgramExecutionContext(
            ProgramNode program, 
            CommandCollection commands)
        {
            _program = program;
            _commands = commands;
            _variableScopes = new Stack<VariableCollection>();
        }

        public IEnumerator<IExecutionEvent> Evaluate()
        {
            _globalScope = new VariableCollection(null);
            var scope = new VariableCollection(_globalScope);
            _variableScopes.Push(scope);
            
            foreach (var statement in _program.statements)
            {
                foreach (var progress in EvalStatement(statement))
                {
                    yield return progress;
                }
            }
        }

        private IEnumerable<IExecutionEvent> EvalStatement(IStatementNode statement)
        {
            switch (statement)
            {
                case DeclarationStatement decl:
                    foreach (var p in EvalDeclarationStatement(decl)) yield return p;
                    break;
                case AssignmentStatement assign:
                    foreach (var p in EvalAssignmentStatement(assign)) yield return p;
                    break;
                default:
                    yield return new ExecutionException("Invalid statement", statement);
                    yield break;
            }
            
            yield return null;
        }

        private IEnumerable<IExecutionEvent> EvalDeclarationStatement(DeclarationStatement decl)
        {
            var scopeType = decl.scopeType;
            if (scopeType == DeclarationScopeType.Default)
            {
                // TODO: if it is an array, then we should do global
                scopeType = DeclarationScopeType.Local;
            }

            var scope = scopeType == DeclarationScopeType.Global ? _globalScope : _variableScopes.Peek();

            if (scope.TryDeclareVariable(
                    decl.variable, 
                    decl, 
                    out var variable, 
                    out var error))
            {
                yield return null; // TODO: should we return tracking data?
            }
            else
            {
                yield return error;
            }
        }

        private IEnumerable<IExecutionEvent> EvalAssignmentStatement(AssignmentStatement assign)
        {
            var scope = _variableScopes.Peek();

            if (!scope.TryGetVariable(assign.variable.variableName, out var variable))
            {
                // TODO: ah, it didn't exist, so we need to manually declare it!
                throw new NotImplementedException("implicit declares are not supported yet");
            }
            
            // now we need to evaluate the expression to stick the value into the variable.
            foreach (var p in EvalExpression(assign.expression))
            {
                yield return p;
            }
            
        }

        private IEnumerable<IExecutionEvent> EvalExpression(IExpressionNode expr)
        {
            switch (expr)
            {
                case LiteralIntExpression intExpr:
                    // TODO: return the value somehow?
                    break;
                default:
                    yield return new ExecutionException("Invalid expression", expr);
                    yield break;
            }
        }
    }
}
using System.Collections.Generic;
using DarkBasicYo.Ast;

namespace DarkBasicYo.Machine
{
    public class RuntimeVariable
    {
        public string name;
        public string type;
        public IAstNode source;
    }
    
    public class VariableCollection
    {
        private readonly VariableCollection _parentScope;

        public Dictionary<string, RuntimeVariable> Variables;

        public VariableCollection(VariableCollection parentScope)
        {
            _parentScope = parentScope;
            Variables = new Dictionary<string, RuntimeVariable>();
        }

        public bool TryGetVariable(string name, out RuntimeVariable variable)
        {
            variable = null;
            if (!Variables.TryGetValue(name, out variable))
            {
                // look in parent scope...
                if (_parentScope != null)
                {
                    return _parentScope.TryGetVariable(name, out variable);
                }

                return false;
            }

            return true;
        }

        public bool TryDeclareVariable(string name, IAstNode source, out RuntimeVariable variable, out ExecutionException error)
        {
            variable = null;
            error = null;
            
            if (Variables.TryGetValue(name, out var existing))
            {
                error = new DuplicateDeclarationExecutionException("Variable already declared on", existing.source);
                return false;
            }

            variable = Variables[name] = new RuntimeVariable
            {
                name = name,
                source = source
            };
            return true;
        }
    }

    public class DuplicateDeclarationExecutionException : ExecutionException
    {

        public DuplicateDeclarationExecutionException(string message, IAstNode programSource)
            : base(message, programSource)
        {
        }
    }
}
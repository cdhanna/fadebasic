using System;
using System.Collections.Generic;

namespace FadeBasic.Ast
{
    public interface IAstNode : ICanHaveErrors
    {
        Token StartToken { get; }
        Token EndToken { get; }
        TypeInfo ParsedType { get; }

        Symbol DeclaredFromSymbol { get; }
    }

    public struct TypeInfo
    {
        public VariableType type;
        public string structName;
        public int rank; // when positive, its an array.
        public bool IsArray => rank > 0;

        public static TypeInfo FromVariableType(VariableType variableType, int rank=0, string structName=null) => new TypeInfo
        {
            type = variableType,
            structName = structName,
            rank = rank
        };
        
        public static readonly TypeInfo Void = new TypeInfo { type = VariableType.Void };
        public static readonly TypeInfo Int = new TypeInfo { type = VariableType.Integer };

    } 
    
    public abstract class AstNode : IAstNode, IAstVisitable
    {
        public Token startToken;
        public Token endToken;

        public Token StartToken => startToken;
        public Token EndToken => endToken;

        public List<ParseError> Errors { get; set; } = new List<ParseError>();
        public Symbol DeclaredFromSymbol { get; set; }
        
        public TypeInfo ParsedType { get; set; } = TypeInfo.Void;

        public void ApplyTypeFromSymbol(Symbol symbol)
        {
            if (symbol == null) return;
            ParsedType = symbol.typeInfo;
        }
        
        protected AstNode()
        {

        }

        protected AstNode(Token start, Token end)
        {
            startToken = start;
            endToken = end;
        }

        protected AstNode(Token token) : this(token, token)
        {

        }

        public override string ToString()
        {
            return $"({GetString()})";
        }

        protected abstract string GetString();


        public abstract IEnumerable<IAstVisitable> IterateChildNodes();



        public IAstVisitable FindFirst(Func<IAstVisitable, bool> predicate)
        {
            if (predicate(this))
            {
                return this;
            }
            var nodes = IterateChildNodes();
            foreach (var node in nodes)
            {
                if (node == null) continue;
                var found = node.FindFirst(predicate);
                if (found != null)
                {
                    return found;
                }
            }

            return null;
        }
        
        public void Visit(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit=null)
        {
            onVisit(this);
            var nodes = IterateChildNodes();
            foreach (var node in nodes)
            {
                if (node == null) continue;
                node.Visit(onVisit, onExit);
            }
            onExit?.Invoke(this);
        }
    }


    public interface IAstVisitable : IAstNode
    {
        IEnumerable<IAstVisitable> IterateChildNodes();

        public void Visit(Action<IAstVisitable> onVisit, Action<IAstVisitable> onExit=null);
        public IAstVisitable FindFirst(Func<IAstVisitable, bool> predicate);
        // {
        //     onVisit(this);
        //     var nodes = IterateChildNodes();
        //     foreach (var node in nodes)
        //     {
        //         if (node == null) continue;
        //         node.Visit(onVisit);
        //     }
        // }
    }

    public static class ErrorVisitorExtensions
    {
        public static List<ParseError> GetAllErrors(this IAstVisitable visitable)
        {
            var errors = new List<ParseError>();
            visitable.Visit(child =>
            {
                if (child.Errors != null && child.Errors.Count > 0)
                    errors.AddRange(child.Errors);
            });
            return errors;
        }
    }
}
using System;
using System.Collections.Generic;

namespace DarkBasicYo.Ast
{
    public interface IAstNode : ICanHaveErrors
    {
        Token StartToken { get; }
        Token EndToken { get; }
    }
    public abstract class AstNode : IAstNode
    {
        public Token startToken;
        public Token endToken;

        public Token StartToken => startToken;
        public Token EndToken => endToken;

        public List<ParseError> Errors { get; set; } = new List<ParseError>();
        
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

    }


    public interface IAstVisitable : IAstNode
    {
        IEnumerable<IAstVisitable> IterateChildNodes();

        public void Visit(Action<IAstVisitable> onVisit)
        {
            onVisit(this);
            var nodes = IterateChildNodes();
            foreach (var node in nodes)
            {
                if (node == null) continue;
                node.Visit(onVisit);
            }
        }
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
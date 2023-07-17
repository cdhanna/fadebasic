using System;
using System.Collections.Generic;
using DarkBasicYo.Ast;

namespace DarkBasicYo
{

    public class ParserException : Exception
    {
        public string Message { get; }
        public Token Start { get; }
        public Token End { get; }

        public ParserException(string message, Token start, Token end = null)
            : base($"Parse Exception: {message} at {start.Location}-{end?.Location}")
        {
            Message = message;
            Start = start;
            End = end;
        }
    }

    public class Parser
    {
        private readonly TokenStream _stream;
        private readonly CommandCollection _commands;


        public Parser(TokenStream stream, CommandCollection commands)
        {
            _stream = stream;
            _commands = commands;
        }

        public ProgramNode ParseProgram()
        {
            var program = new ProgramNode(_stream.Current);

            while (!_stream.IsEof)
            {
                var statement = ParseStatement();
                program.statements.Add(statement);
            }


            program.endToken = _stream.Current;
            return program;
        }

        // private IStatementNode ParseRealAssignment(Token startToken)
        // {
        //     var token = _stream.Advance();
        //     switch (token.type)
        //     {
        //         case LexemType.OpEqual:
        //             var expr = ParseExpression();
        //             return new AssignmentStatement
        //             {
        //                 startToken = startToken,
        //                 endToken = _stream.Current,
        //                 variable = new VariableRefNode(token),
        //                 expression = expr,
        //             };
        //         default:
        //             throw new ParserException("Expected equals", token);
        //     }
        // }

        private IStatementNode ParseStatement()
        {
            IStatementNode Inner()
            {
                var token = _stream.Advance();
                switch (token.type)
                {
                    // case LexemType.KeywordEndWhile:
                    //     return new EndWhileStatement(token);
                    // case LexemType.EndStatement:
                    //     // a ; character doesn't _do_ anything other than stop the statement.
                    //     return null;
                    case LexemType.KeywordWhile:

                        // parse the condition expression...
                        var whileConditionExpr = ParseWikiExpression();
                        // parse the statements until there is an end-while.

                        // ignore the EoS
                        _stream.Advance();
                        
                        var whileStatements = new List<IStatementNode>();
                        IStatementNode endStatement = null;
                        while (_stream.Peek.type != LexemType.KeywordEndWhile)
                        {
                            var statement = ParseStatement();
                            whileStatements.Add(statement);
                        }

                        // discard the endwhile token.
                        _stream.Advance();

                        return new WhileStatement
                        {
                            condition = whileConditionExpr,
                            statements = whileStatements,
                            startToken = token,
                            endToken = _stream.Current
                        };

                        break;
                    case LexemType.VariableReal:
                    // return ParseRealAssignment(token);
                    case LexemType.VariableGeneral:

                        var secondToken = _stream.Advance();

                        switch (secondToken.type)
                        {
                            case LexemType.OpEqual:
                                var expr = ParseWikiExpression();
                                return new AssignmentStatement
                                {
                                    startToken = token,
                                    endToken = _stream.Current,
                                    variable = new VariableRefNode(token),
                                    expression = expr,
                                };
                            case LexemType.KeywordAs:
                                var type = ParseTypeReference();
                                return new DeclarationStatement
                                {
                                    startToken = token,
                                    endToken = _stream.Current,
                                    type = type,
                                    variable = token.raw
                                };
                                break;
                            default:
                                throw new Exception("parser exception! Unknown statement");
                        }

                        break;

                    case LexemType.CommandWord:
                        // resolve the command using the command index....

                        if (!_commands.TryGetCommandDescriptor(token, out var command))
                        {
                            throw new Exception("Parser exception! unknown command " + token.raw);
                        }

                        // parse the args!
                        var argExpressions = new List<IExpressionNode>();
                        foreach (var argDescriptor in command.args)
                        {
                            // TODO: check for optional or arity?
                            var argExpr = ParseWikiExpression();
                            argExpressions.Add(argExpr);
                        }

                        return new CommandStatement
                        {
                            startToken = token,
                            endToken = _stream.Current,
                            command = command,
                            args = argExpressions
                        };

                        break;
                    default:
                        throw new ParserException($"Unknown token type=[{token.type}]", token);
                }

                /*
                 * a statement can be one of the following,
                 * 1. assignmentStatement
                 * 2. commandStatement
                 * 3. ifStatement
                 * 4. forStatement
                 * 5. repeatStatement
                 * 
                 */
            }

            var result = Inner();
            _stream.Advance(); // ignore the end statement.
            return result;
        }

        private TypeReferenceNode ParseTypeReference()
        {
            var token = _stream.Advance();
            switch (token.type)
            {
                case LexemType.KeywordTypeByte:
                case LexemType.KeywordTypeBoolean:
                case LexemType.KeywordTypeFloat:
                case LexemType.KeywordTypeInteger:
                case LexemType.KeywordTypeString:
                case LexemType.KeywordTypeWord:
                case LexemType.KeywordTypeDWord:
                case LexemType.KeywordTypeDoubleFloat:
                case LexemType.KeywordTypeDoubleInteger:
                    return new TypeReferenceNode(token);
                default:
                    throw new Exception("Parser exception! Expected type reference");
            }
        }

        private int GetOperatorOrder(Token token)
        {
            switch (token.type)
            {
                case LexemType.OpGt:
                case LexemType.OpLt:
                    return 1;
                case LexemType.OpMultiply:
                case LexemType.OpDivide:
                    return 3;
                case LexemType.OpMinus:
                case LexemType.OpPlus:
                    return 2;
                default:
                    throw new ParserException("Invalid lexem type for op order", token);
            }
        }
        private bool IsLeftAssoc(Token token)
        {
            switch (token.type)
            {
                case LexemType.OpDivide:
                case LexemType.OpMinus:
                case LexemType.OpGt:
                case LexemType.OpLt:
                    return false;
                case LexemType.OpPlus:
                case LexemType.OpMultiply:
                case LexemType.OpEqual:
                    return true;
                default:
                    throw new ParserException("Invalid lexem type for op assoc", token);
            }
        }

        private bool IsBinaryOp(Token token)
        {
            switch (token.type)
            {
                case LexemType.OpMultiply:
                case LexemType.OpPlus:
                case LexemType.OpDivide:
                case LexemType.OpMinus:
                case LexemType.OpLt:
                case LexemType.OpGt:
                case LexemType.OpEqual:
                    return true;
                default:
                    return false;
            }
        }

        public IExpressionNode ParseWikiExpression()
        {
            var term = ParseWikiTerm();
            return ParseWikiExpression(term, 0);
        }
        private IExpressionNode ParseWikiExpression(IExpressionNode lhs, int minPrec)
        {
        
            var lookAhead = _stream.Peek;
            while (IsBinaryOp(lookAhead) && GetOperatorOrder(lookAhead) >= minPrec)
            {
                var op = lookAhead;
                _stream.Advance();
                var rhs = ParseWikiTerm();
                lookAhead = _stream.Peek;
                // while lookahead is a binary operator whose precedence is greater
                //     than op's, or a right-associative operator
                //     whose precedence is equal to op's
                while (IsBinaryOp(lookAhead) && (
                        (GetOperatorOrder(lookAhead) > GetOperatorOrder(op)) ||
                        (IsLeftAssoc(lookAhead) && GetOperatorOrder(lookAhead) == GetOperatorOrder(op))
                       ))
                {
                    //rhs := parse_expression_1 (rhs, precedence of op + (1 if lookahead precedence is greater, else 0))
                    var augment = GetOperatorOrder(lookAhead) > GetOperatorOrder(op) ? 1 : 0;
                    rhs = ParseWikiExpression(rhs, GetOperatorOrder(op) + augment);
                    lookAhead = _stream.Peek;
                }

                lhs = new BinaryOperandExpression(start: null, end: null, op: op, lhs: lhs, rhs: rhs);
            }
            
            return lhs;
        }

        public IExpressionNode ParseWikiTerm()
        {
            var token = _stream.Advance();
            switch (token.type)
            {
                case LexemType.ParenOpen:
                    var expr = ParseWikiExpression();
                    var closeToken = _stream.Advance(); // move past closing...

                    if (closeToken.type != LexemType.ParenClose)
                    {
                        throw new ParserException("expected closing paren", closeToken);
                    }

                    return expr;
                    
                    break;
                
                case LexemType.VariableReal:
                case LexemType.VariableString:
                case LexemType.VariableGeneral:
                    return new VariableRefNode(token);
                case LexemType.LiteralInt:
                    return new LiteralIntExpression(token);
                case LexemType.LiteralReal:
                    return new LiteralRealExpression(token);
                default:
                    throw new ParserException("Cannot match single, " + token.type, token);
            }
        }
        
    }
}
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
            : base($"Parse Exception: {message} at {start.Location}-{end?.Location}(${start.lexem.type})")
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
                switch (statement)
                {
                    case TypeDefinitionStatement typeStatement:
                        program.typeDefinitions.Add(typeStatement);
                        break;
                    case LabelDeclarationNode labelStatement:
                        program.labels.Add(new LabelDefinition
                        {
                            statementIndex = program.statements.Count + 1,
                            node = labelStatement
                        });
                        program.statements.Add(labelStatement);
                        break;
                    default:
                        program.statements.Add(statement);
                        break;
                }
            }


            program.endToken = _stream.Current;
            return program;
        }
        

        private IStatementNode ParseStatementThatStartsWithScope(Token scopeToken)
        {
            // we know this must be a declaration node.

            var next = _stream.Advance();
            switch (next.type)
            {
                case LexemType.VariableGeneral:
                    // we know we have something that looks like "local x", so the next token MUST be "as", and then there MUST be a type.
                    var asToken = _stream.Advance();
                    if (asToken.type != LexemType.KeywordAs)
                    {
                        throw new ParserException("expected keyword, as", scopeToken, asToken);
                    }
                    var typeReference = ParseTypeReference();
                    return new DeclarationStatement(scopeToken, new VariableRefNode(next), typeReference);
                    
                case LexemType.KeywordDeclareArray:
                    var decl = ParseDimStatement(next);
                    return new DeclarationStatement(scopeToken, decl);
                    throw new NotImplementedException("scope array");
                    break;
                default:
                    throw new ParserException("Expected either variable or dim ", scopeToken, next);
            }
        }

        // private IStatementNode ParseScopeVariableDecl(Token scopeToken, Token variableToken)
        // {
        //     // stuff that looks like local x as byte
        //     
        //     
        // }

        private DeclarationStatement ParseDimStatement(Token dimToken)
        {
            // dim
            var next = _stream.Advance();
            switch (next.type)
            {
                case LexemType.VariableString:
                case LexemType.VariableReal:
                case LexemType.VariableGeneral:
                    // dim x
                    var openParenToken = _stream.Advance();
                    if (openParenToken.type != LexemType.ParenOpen)
                    {
                        throw new ParserException("Expected open paren ", openParenToken);
                    }

                    var rankExpressions = new List<IExpressionNode>();
                    for (var rankIndex = 0; rankIndex < 5; rankIndex++) // 5 is the magic "max dimension"
                    {
                        var rankExpression = ParseWikiExpression();
                        rankExpressions.Add(rankExpression);
                        var closeOrComma = _stream.Advance();
                        var closeFound = false;
                        switch (closeOrComma.type)
                        {
                            case LexemType.ParenClose:
                                closeFound = true;
                                break;
                            case LexemType.ArgSplitter:
                                if (rankIndex + 1 == 5)
                                {
                                    throw new ParserException("arrays can only have 5 dimensions", closeOrComma);
                                }
                                break;
                            default:
                                throw new ParserException("Expected close paren or comma", openParenToken);
                        }

                        if (closeFound)
                        {
                            break;
                        }
                    }

                    // so far, we have dim x(n)
                    // next, we _could_ have an AS, or it could be an end-of-statement

                    if (_stream.Peek?.type == LexemType.KeywordAs)
                    {
                        _stream.Advance(); // discard the "as"
                        var typeReference = ParseTypeReference();
                        return new DeclarationStatement(dimToken, new VariableRefNode(next), typeReference,
                            rankExpressions.ToArray());
                    }
                    
                    return new DeclarationStatement(
                        dimToken, 
                        new VariableRefNode(next), 
                        rankExpressions.ToArray());
                    break;
                default:
                    throw new ParserException("Expected variable declaration ", next);
            }
        }

        public IVariableNode ParseVariableReference(Token token=null)
        {
            if (token == null)
            {
                token = _stream.Advance();
            }
            switch (token.type)
            {
                case LexemType.OpMultiply:
                    var ptrExpression = ParseVariableReference();
                    return new DeReference(ptrExpression, token);
                
                case LexemType.VariableString:
                case LexemType.VariableReal:
                case LexemType.VariableGeneral:
                    
                    // cooool...

                    var next = _stream.Peek;
                    switch (next.type)
                    {
                        case LexemType.FieldSplitter:
                            // ah, actually, this whole thing is a reference!
                            _stream.Advance();

                            var rhs = ParseVariableReference();
                            return new StructFieldReference
                            {
                                startToken = token, endToken = _stream.Current,
                                right = rhs, left = new VariableRefNode(token)
                            };
                            
                            break;
                        case LexemType.ParenOpen:
                            _stream.Advance();
                            var rankExpressions = new List<IExpressionNode>();
                            for (var rankIndex = 0; rankIndex < 5; rankIndex++) // 5 is the magic "max dimension"
                            {
                                var rankExpression = ParseWikiExpression();
                                rankExpressions.Add(rankExpression);
                                var closeOrComma = _stream.Advance();
                                var closeFound = false;
                                switch (closeOrComma.type)
                                {
                                    case LexemType.ParenClose:
                                        closeFound = true;
                                        break;
                                    case LexemType.ArgSplitter:
                                        if (rankIndex + 1 == 5)
                                        {
                                            throw new ParserException("arrays can only have 5 dimensions", closeOrComma);
                                        }
                                        break;
                                    default:
                                        throw new ParserException("Expected close paren or comma", next);
                                }

                                if (closeFound)
                                {
                                    break;
                                }
                            }
                            
                            // we have the ranks, so we know its a array access at least.
                            // but it could still be a nested thing

                            var indexValue = new ArrayIndexReference
                            {
                                startToken = token,endToken = _stream.Current,
                                rankExpressions = rankExpressions,
                                variableName = token.raw
                            };
                            
                            if (_stream.Peek?.type == LexemType.FieldSplitter)
                            {
                                _stream.Advance();

                                var arrayRhs = ParseVariableReference();
                                return new StructFieldReference
                                {
                                    startToken = token, endToken = _stream.Current,
                                    right = arrayRhs, left = indexValue
                                };

                            }

                            return indexValue;
                            
                            break;
                        
                        default:
                            // okay, it is just a regular variable...
                            return new VariableRefNode(token);
                    }
                    
                    break;
                default:
                    throw new ParserException("expected a variable reference", token);
            }
            
        }

        private List<IExpressionNode> ParseCommandArgs(Token token, CommandDescriptor command)
        {
            var argExpressions = new List<IExpressionNode>();
            for (var i = 0; i < command.args.Count; i++)
            {
                
                var argDescriptor = command.args[i];
                
                if (i > 0)
                {
                    // expect an arg separator
                    var commaToken = _stream.Peek;
                    if (commaToken.type != LexemType.ArgSplitter)
                    {
                        if (argDescriptor.isOptional)
                        {
                            return argExpressions;
                        }
                        throw new ParserException("Expected a comma to separate args", commaToken);
                    }

                    _stream.Advance(); // discard the ,
                }
                
                
                // TODO: check for optional or arity?
                if (argDescriptor.isRef)
                {
                    var variableReference = ParseVariableReference();
                    argExpressions.Add(new AddressExpression(variableReference, token));
                }
                else
                {
                    var argExpr = ParseWikiExpression();
                    argExpressions.Add(argExpr);
                }

            
            }

            return argExpressions;
        }


        private IStatementNode ParseStatement()
        {
            /*
             * Valid:
             * -----
             * 
             * x = 3
             * x as byte
             * local x as byte
             * global x as byte
             * dim x(3)
             * dim x(3) as byte
             * local dim x(3) as byte
             * global dim x(3) as byte
             * local dim x(3)
             * global dim x(3)
             * x(3) = 2
             * x.y = 2
             *
             * Invalid:
             * -------
             *
             * local x = 3
             * dim x(3) = 1
             * local x.y = 1
             * dim local
             */
            
            
            IStatementNode Inner()
            {
                
                var token = _stream.Advance();
                IStatementNode subStatement = null;
                switch (token.type)
                {
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

                    case LexemType.KeywordGoto:
                        return ParseGoto(token);
                    case LexemType.KeywordGoSub:
                        return ParseGoSub(token);
                    case LexemType.KeywordReturn:
                        return ParseReturn(token);
                    case LexemType.KeywordEnd:
                        return new EndProgramStatement(token);
                    case LexemType.KeywordScope:
                        return ParseStatementThatStartsWithScope(token);
                    case LexemType.KeywordDeclareArray:
                        return ParseDimStatement(token);
                    case LexemType.VariableReal:
                    case LexemType.VariableString:
                    case LexemType.VariableGeneral:

                        var reference = ParseVariableReference(token);
                        
                        var secondToken = _stream.Advance();

                        switch (secondToken.type)
                        {
                            case LexemType.Colon:
                                return new LabelDeclarationNode(token, secondToken);
                                break;
                            case LexemType.OpEqual:
                                var expr = ParseWikiExpression();
                                
                                // we actually need to emit a declaration node and an assignment. 
                                return new AssignmentStatement
                                {
                                    startToken = token,
                                    endToken = _stream.Current,
                                    variable = reference,
                                    expression = expr,
                                };
                            case LexemType.KeywordAs:
                                var type = ParseTypeReference();
                                // TODO: if the type is an array, then we should make the scope global by default.
                                var scopeType = DeclarationScopeType.Local;
                                return new DeclarationStatement
                                {
                                    startToken = token,
                                    endToken = _stream.Current,
                                    type = type,
                                    scopeType = scopeType,
                                    variable = token.raw
                                };
                            default:
                                throw new Exception("parser exception! Unknown statement, " + secondToken.type);
                        }

                        break;

                    case LexemType.CommandWord:
                        // resolve the command using the command index....
                        if (!_commands.TryGetCommandDescriptor(token, out var command))
                        {
                            throw new Exception("Parser exception! unknown command " + token.raw);
                        }

                        // parse the args!
                        var argExpressions = ParseCommandArgs(token, command);
                        return new CommandStatement
                        {
                            startToken = token,
                            endToken = _stream.Current,
                            command = command,
                            args = argExpressions
                        };

                    case LexemType.KeywordType:
                        return ParseTypeDefinition(token);
                        
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

        private GotoStatement ParseGoto(Token gotoToken)
        {
            var next = _stream.Advance();
            switch (next.type)
            {
                case LexemType.VariableGeneral:
                    return new GotoStatement(gotoToken, next);
                    
                default:
                    throw new ParserException("Expected label for goto statement", gotoToken, next);
            }
        }
        private GoSubStatement ParseGoSub(Token gotoToken)
        {
            var next = _stream.Advance();
            switch (next.type)
            {
                case LexemType.VariableGeneral:
                    return new GoSubStatement(gotoToken, next);
                    
                default:
                    throw new ParserException("Expected label for goto statement", gotoToken, next);
            }
        }
        
        private ReturnStatement ParseReturn(Token token)
        {
            return new ReturnStatement(token);
        }
        
        private TypeDefinitionMember ParseTypeMember()
        {
            var token = _stream.Advance();
            switch (token.type)
            {
                case LexemType.VariableString:
                case LexemType.VariableReal:
                case LexemType.VariableGeneral:
                    var variable = new VariableRefNode(token);
                    // optionally, we can parse an "AS TYPE" style expression...
                    if (_stream.Peek.type == LexemType.KeywordAs)
                    {
                        _stream.Advance(); // discard the "AS"
                        var type = ParseTypeReference();
                        return new TypeDefinitionMember(token, _stream.Current, variable, type);
                    }
                    else
                    {
                        return new TypeDefinitionMember(token, _stream.Current, variable,
                            new TypeReferenceNode(_stream.Current));
                    }
                    break;
                default:
                    throw new ParserException("Expected a variable name", token);
            }
        }

        private TypeDefinitionStatement ParseTypeDefinition(Token start)
        {
            var nameToken = _stream.Advance();
            switch (nameToken.type)
            {
                case LexemType.VariableGeneral:

                    var members = new List<TypeDefinitionMember>();
                    var name = new VariableRefNode(nameToken);

                    var lookingForMembers = true;
                    while (lookingForMembers)
                    {
                        var peek = _stream.Peek;
                        switch (peek.type)
                        {
                            case LexemType.EOF:
                                throw new ParserException("Hit end of file without a closing type statement", peek);
                            case LexemType.EndStatement:
                                _stream.Advance();
                                break;
                            case LexemType.KeywordEndType:
                                _stream.Advance();
                                lookingForMembers = false;
                                break;
                            default:
                                var member = ParseTypeMember();
                                members.Add(member);
                                break;
                        }
                    }

                    return new TypeDefinitionStatement(start, _stream.Current, name, members);
                    break;
                default:
                    throw new ParserException("expected a type name", nameToken);
            }
        }

        private ITypeReferenceNode ParseTypeReference()
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
                
                case LexemType.VariableGeneral:
                    return new StructTypeReferenceNode(new VariableRefNode(token));
                default:
                    throw new ParserException("Parser exception! Expected type reference, but found " + token.type, token);
            }
        }

        private int GetOperatorOrder(Token token)
        {
            switch (token.type)
            {
                case LexemType.KeywordNot:
                    return 5;
                case LexemType.KeywordAnd:
                    return 6;
                case LexemType.KeywordOr:
                    return 7;
                case LexemType.OpGt:
                case LexemType.OpLt:
                case LexemType.OpGte:
                case LexemType.OpLte:
                case LexemType.OpEqual:
                case LexemType.OpNotEqual:
                    return 10;
                case LexemType.OpMinus:
                case LexemType.OpPlus:
                    return 20;
        
                case LexemType.OpMultiply:
                case LexemType.OpDivide:
                    return 30;
                
                case LexemType.OpMod:
                case LexemType.OpPower:
                    return 40;
           
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
                case LexemType.OpNotEqual:
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

        private IExpressionNode ParseWikiTerm()
        {
            var token = _stream.Advance();
            switch (token.type)
            {
                case LexemType.CommandWord:
                    
                    if (!_commands.TryGetCommandDescriptor(token, out var command))
                    {
                        throw new Exception("Parser exception! unknown command " + token.raw);
                    }

                    // parse the args!
                    var argExpressions = ParseCommandArgs(token, command);

                    // var argExpressions = new List<IExpressionNode>();
                    // foreach (var argDescriptor in command.args)
                    // {
                    //     // TODO: check for optional or arity?
                    //
                    //     if (argDescriptor.isRef)
                    //     {
                    //         // parse a very specific type of expression, it must be a variable...
                    //         var variableReference = ParseVariableReference();
                    //         argExpressions.Add(new AddressExpression(variableReference, token));
                    //     }
                    //     else
                    //     {
                    //         var argExpr = ParseWikiExpression();
                    //         argExpressions.Add(argExpr);
                    //     }
                    //     
                    // }

                    return new CommandExpression()
                    {
                        startToken = token,
                        endToken = _stream.Current,
                        command = command,
                        args = argExpressions
                    };
                    
                    break;
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
                    return ParseVariableReference(token);
                case LexemType.LiteralInt:
                    return new LiteralIntExpression(token);
                case LexemType.LiteralReal:
                    return new LiteralRealExpression(token);
                case LexemType.LiteralString:
                    return new LiteralStringExpression(token);
                case LexemType.OpMultiply:
                    var deRefExpr = ParseVariableReference();
                    return new DereferenceExpression(deRefExpr, token);
                default:
                    throw new ParserException("Cannot match single, " + token.type, token);
            }
        }
        
    }
}
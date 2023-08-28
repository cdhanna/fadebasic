using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using DarkBasicYo.Ast;
using DarkBasicYo.Virtual;

namespace DarkBasicYo
{

    public class CompilerException : Exception
    {
        public CompilerException(string message, IAstNode node) 
        :base ($"Compiler Exception: {message} at {node.StartToken.Location}-{node.EndToken?.Location}({node.ToString()})")
        {
            
        }
    }
    
    public class ParserException : Exception
    {
        public string Message { get; }
        public Token Start { get; }
        public Token End { get; }

        public ParserException(string message, Token start, Token end = null)
            : base($"Parse Exception: {message} at {start.Location}-{end?.Location}({start.lexem.type})")
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
                            
                            // var statements = new List<IStatementNode>();
                            var looking = true;
                            while (looking)
                            {
                                var nextToken = _stream.Peek;
                                switch (nextToken.type)
                                {
                                    case LexemType.EOF:
                                        throw new ParserException("Hit end of file without a closing paren", nextToken);
                                    case LexemType.EndStatement:
                                    case LexemType.ArgSplitter:
                                        _stream.Advance();
                                        break;
                                    case LexemType.ParenClose:
                                        _stream.Advance();
                                        looking = false;
                                        break;
                                    default:
                                        var member = ParseWikiExpression();
                                        rankExpressions.Add(member);
                                        break; 
                                }
                            }
                            
                            // for (var rankIndex = 0; rankIndex < 5; rankIndex++) // 5 is the magic "max dimension"
                            // {
                            //     var rankExpression = ParseWikiExpression();
                            //     rankExpressions.Add(rankExpression);
                            //     var closeOrComma = _stream.Advance();
                            //     var closeFound = false;
                            //     switch (closeOrComma.type)
                            //     {
                            //         case LexemType.ParenClose:
                            //             closeFound = true;
                            //             break;
                            //         case LexemType.ArgSplitter:
                            //             if (rankIndex + 1 == 5)
                            //             {
                            //                 throw new ParserException("arrays can only have 5 dimensions", closeOrComma);
                            //             }
                            //             break;
                            //         default:
                            //             throw new ParserException("Expected close paren or comma", next);
                            //     }
                            //
                            //     if (closeFound)
                            //     {
                            //         break;
                            //     }
                            // }
                            
                            // we have the ranks, so we know its a array access at least.
                            // but it could still be a nested thing

                            var indexValue = new ArrayIndexReference
                            {
                                startToken = token,endToken = _stream.Current,
                                rankExpressions = rankExpressions,
                                variableName = token.caseInsensitiveRaw
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

        private List<IExpressionNode> ParseAvailableArgs(Token token)
        {
            // the rule is, just parse terms while there are commas....
            var args = new List<IExpressionNode>();
            
            bool isOpenParen = _stream.Peek.type == LexemType.ParenOpen;
            if (isOpenParen)
            {
                // discard
                _stream.Advance();
            }

            void EnsureCloseParen()
            {
                if (isOpenParen)
                {
                    if (_stream.Advance().type != LexemType.ParenClose)
                    {
                        throw new ParserException("Expected to find close paren for command if there is an open paren",
                            token, _stream.Current);
                    }
                }
            }
            
            var looking = true;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        looking = false;
                        break;
                    case LexemType.ArgSplitter:
                        _stream.Advance();
                        break;
                    case LexemType.EndStatement:
                        _stream.Advance();
                        looking = false;
                        break;
                    default:
                        if (TryParseExpression(out var nextArg))
                        {
                            args.Add(nextArg);
                        }
                        else
                        {
                            looking = false;
                            
                        }
                        break; 
                }
            }
            
            EnsureCloseParen();
            return args;

        }

        private List<IExpressionNode> ParseCommandArgs(Token token, CommandInfo command)
        {
            var argExpressions = new List<IExpressionNode>();

            bool isOpenParen = _stream.Peek.type == LexemType.ParenOpen;
            if (isOpenParen)
            {
                // discard
                _stream.Advance();
            }

            void EnsureCloseParen()
            {
                if (isOpenParen)
                {
                    if (_stream.Advance().type != LexemType.ParenClose)
                    {
                        throw new ParserException("Expected to find close paren for command if there is an open paren",
                            token, _stream.Current);
                    }
                }
            }

            var parsedCount = 0;
            
            for (var i = 0; i < command.args.Length; i++)
            {
                
                var argDescriptor = command.args[i];
                if (argDescriptor.isVmArg) continue;
                
                if (argDescriptor.isParams)
                {
                    // this must be the last named param, and we read until the end!
                    // nothing is allowed to pass by reference, on a params
                    var huntingForMoreArgs = true;
                    while (huntingForMoreArgs)
                    {
                        if (TryParseExpression(out var expr))
                        {
                            argExpressions.Add(expr);
                            // if the next token is a comma, we can keep going!
                           
                        }
                        else if (_stream.Peek.type == LexemType.ArgSplitter)
                        {
                            _stream.Advance(); // discard the arg-splitter
                        }
                        else
                        {
                            huntingForMoreArgs = false;
                        }
                    }

                    EnsureCloseParen();
                    return argExpressions;
                }
                
                if (parsedCount > 0)
                {
                    // expect an arg separator
                    var commaToken = _stream.Peek;
                    if (commaToken.type != LexemType.ArgSplitter)
                    {
                        if (argDescriptor.isOptional)
                        {
                            EnsureCloseParen();
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
                    if (TryParseExpression(out var argExpr))
                    {
                        argExpressions.Add(argExpr);
                    } else if (!argDescriptor.isOptional)
                    {
                        throw new ParserException("Required arg, but found none", token, _stream.Current);
                    }
                }
                // else if (!argDescriptor.isOptional)
                // {
                //     var argExpr = ParseWikiExpression();
                //     argExpressions.Add(argExpr);
                // }

                parsedCount++;

            }

            EnsureCloseParen();
            return argExpressions;
        }


        private IStatementNode ParseStatement(bool consumeEndOfStatement=true)
        {
            IStatementNode Inner()
            {
                
                var token = _stream.Advance();
                IStatementNode subStatement = null;
                switch (token.type)
                {
                    case LexemType.KeywordRemStart:

                        var remToken = _stream.Peek;
                        switch (remToken.type)
                        {
                            case LexemType.EndStatement:
                                _stream.Advance(); // move past the rem.
                                break;
                        }
                        remToken = _stream.Peek;
                        switch (remToken.type)
                        {
                            case LexemType.KeywordRemEnd:
                                _stream.Advance(); // move past the rem.
                                break;
                        }

                        return new CommentStatement(token, token.caseInsensitiveRaw);
                        // return new CommentStatement(token, token.raw.Substring("remstart".Length, token.raw.Length - ("remstartremend".Length)));
                        break;
                    case LexemType.KeywordRem when token.caseInsensitiveRaw[0] == '`':
                        return new CommentStatement(token, token.caseInsensitiveRaw.Substring(1));
                    case LexemType.KeywordRem:
                        return new CommentStatement(token, token.caseInsensitiveRaw.Substring(3));
                    case LexemType.KeywordIf:
                        return ParseIfStatement(token);
                    case LexemType.KeywordWhile:
                        return ParseWhileStatement(token);
                    case LexemType.KeywordRepeat:
                        return ParseRepeatUntil(token);
                    case LexemType.KeywordFor:
                        return ParseForStatement(token);
                    case LexemType.KeywordDo:
                        return ParseDoLoopStatement(token);
                    case LexemType.KeywordSelect:
                        return ParseSwitchStatement(token);
                    case LexemType.KeywordGoto:
                        return ParseGoto(token);
                    case LexemType.KeywordGoSub:
                        return ParseGoSub(token);
                    case LexemType.KeywordReturn:
                        return ParseReturn(token);
                    case LexemType.KeywordFunction:
                        return ParseFunction(token);
                    case LexemType.KeywordExitFunction:
                        return ParseExitFunction(token);
                    case LexemType.KeywordEnd:
                        return new EndProgramStatement(token);
                    case LexemType.KeywordExit:
                        return new ExitLoopStatement(token);
                    case LexemType.KeywordScope:
                        return ParseStatementThatStartsWithScope(token);
                    case LexemType.KeywordDeclareArray:
                        return ParseDimStatement(token);
                    // case LexemType.Label:
                    //     return new LabelDeclarationNode(token);
                    case LexemType.VariableReal:
                    case LexemType.VariableString:
                    case LexemType.VariableGeneral:

                        var reference = ParseVariableReference(token);
                        
                        var secondToken = _stream.Advance();

                        switch (secondToken.type)
                        {
                            case LexemType.EndStatement when secondToken.caseInsensitiveRaw == ":":
                                return new LabelDeclarationNode(token, secondToken);
                                break;
                            case LexemType.EndStatement:
                                return new ExpressionStatement(reference); // TODO: eh?

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
                                    variable = token.caseInsensitiveRaw
                                };
                            default:
                                throw new Exception("parser exception! Unknown statement, " + secondToken.type);
                        }

                        break;

                    case LexemType.CommandWord:
                        // resolve the command using the command index....
                        // if (!_commands.TryGetCommandDescriptor(token, out var possibleCommands))
                        // {
                        //     throw new Exception("Parser exception! unknown command " + token.caseInsensitiveRaw);
                        // }
                        //
                        // possibleCommands = possibleCommands.ToList(); // make a copy!
                        //
                        // // parse the args!
                        // // var argExpressions = ParseCommandArgs(token, command);
                        // var commandArgs = ParseAvailableArgs(token);
                        //
                        // // now, based on the args that EXIST, we need to boil down the possible commands...
                        // var invalidCommands = new List<CommandInfo>();
                        //
                        // foreach (var possible in possibleCommands)
                        // {
                        //     // each arg needs to line up to something that was actually parsed
                        //     var j = 0; // j is the index into the parsed commands
                        //     for (var i = 0; i < possible.args.Length; i++)
                        //     {
                        //         var expected = possible.args[i];
                        //         if (expected.isVmArg) continue; // we don't expect to see vms in the parsed expression
                        //
                        //         if (expected.isParams) // defacto, we know this MUST be the last index
                        //         {
                        //             if (i != possible.args.Length - 1)
                        //                 throw new ParserException("how can there be a params arg that is not last?",
                        //                     token);
                        //             
                        //             // at this point, all remaining expressions are allows to sit here.
                        //             j = i;
                        //             continue;
                        //         }
                        //
                        //         // okay, we are parsing!
                        //         j++;
                        //     }
                        //
                        //     if (j != possible.args.Length - 1)
                        //     {
                        //         invalidCommands.Add(possible);
                        //     }
                        // }
                        //
                        // possibleCommands = possibleCommands.Except(invalidCommands).ToList();
                        //
                        // if (possibleCommands.Count == 0)
                        // {
                        //     throw new ParserException("There was an error matching the args to the available command",
                        //         token);
                        // }
                        //
                        // if (possibleCommands.Count > 1)
                        // {
                        //     throw new ParserException(
                        //         "There are too many possible overloads for the method call, and it is ambigious",
                        //         token);
                        // }
                        //
                        // ParseCommandOverload(token, out var command, out var commandArgs);
                        ParseCommandOverload2(token, out var command, out var commandArgs);
                        return new CommandStatement
                        {
                            startToken = token,
                            endToken = _stream.Current,
                            command = command,
                            args = commandArgs
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
   
            switch (_stream.Peek.type)
            {
                case LexemType.EndStatement when consumeEndOfStatement:
                    _stream.Advance();
                    break;
                default:
                    break;
            }
            
            return result;
        }


        private static bool IsValidArgCollection(CommandInfo command, List<IExpressionNode> args)
        {
            // does the list of args match the required args?

            var argPointer = 0;
            for (var i = 0; i < command.args.Length; i++)
            {
                var required = command.args[i];

                if (required.isVmArg)
                {
                    continue; 
                }

                if (required.isParams)
                {
                    // we can read the rest of the args
                    for (var j = argPointer; j < args.Count; j++)
                    {
                        argPointer++;
                    }
                    continue;
                }

                if (required.isOptional)
                {
                    // we may or may not need this...
                    if (argPointer < args.Count)
                    {
                        argPointer++; // there is a value
                    }
                    continue; // whatever, it was optional!
                }

                if (argPointer <= args.Count)
                {
                    argPointer++;
                }
                else
                {
                    return false;
                }
            }

            if (argPointer != args.Count)
            {
                return false; // too many args!
            }
            // if (argPointer != command.ar)
            
            return true;

        }

        private bool TryParseCommandFlavor(CommandInfo command, out List<IExpressionNode> args, out int tokenJump)
        {
            var start = _stream.Save();
            var startToken = _stream.Current;
            tokenJump = -1;
            args = new List<IExpressionNode>();
            
            
            bool isOpenParen = _stream.Peek.type == LexemType.ParenOpen;
            if (isOpenParen)
            {
                // discard
                _stream.Advance();
            }

            bool EnsureCloseParen()
            {
                if (isOpenParen)
                {
                    if (_stream.Advance().type != LexemType.ParenClose)
                    {
                        return false;
                    }
                }

                return true;
            }
            
            for (var i = 0; i < command.args.Length; i++)
            {
                var required = command.args[i];

                if (required.isVmArg)
                {
                    continue; // we don't parse these
                }
                
                // okay, we need an argument...
                if (!TryParseExpression(out var expr))
                {
                    // there is no arg!
                    // that can only be okay if this command is optional
                    if (required.isOptional || required.isParams)
                    {
                        continue;
                    } 
                    
                    // whoops, this didn't work out.
                    _stream.Restore(start);
                    return false;
                }
                else
                {
                    // we got an expression, add it to our arg list
                    args.Add(expr);

                    if (required.isParams)
                    {
                        i--; // silly, but, allow this arg to be parsed again, since there can be many params...
                    }
                    
                    // if the next token is an arg splitter, we can accept that...
                    switch (_stream.Peek.type)
                    {
                        case LexemType.ArgSplitter:
                            _stream.Advance();
                            break;
                        default:
                            break; // idk, hopefully it is an expression, I guess?
                    }
                }
            }

            if (!EnsureCloseParen())
            {
                _stream.Restore(start);
                return false;
            }
            tokenJump = _stream.Save();
            _stream.Restore(start);
            return true;
        }

        private void ParseCommandOverload2(Token token, out CommandInfo foundCommand, out List<IExpressionNode> commandArgs)
        {
            /*
             * Okay, so, we need to parse the expressions one at a time, and invalidate commands as we go,
             * until there is only one command left...
             *
             * x = screen width - 5
             * // this isn't obvious if its "screen width with one arg, negative 5",
             *    screen width(-5)
             * //  or "screen width with zero args, minus 5"
             *    screen width() - 5
             *
             * wel'll make the decisions to infer that unless otherwise directed, we assign them as args. 
             *
             * screenwidth ()
             * screenwidth (int screen) // this overload may need an int
             *
             * 
             * 
             */
            if (!_commands.TryGetCommandDescriptor(token, out var possibleCommands))
            {
                throw new Exception("Parser exception! unknown command " + token.caseInsensitiveRaw);
            }

            // var possibleArgs = new List<IExpressionNode>[possibleCommands.Count];
            foundCommand = possibleCommands[0];
            var found = false;
            commandArgs = new List<IExpressionNode>();
            int foundJump = -1;
            for (var i = 0 ; i < possibleCommands.Count; i ++)
            {
                var option = possibleCommands[i];
                if (TryParseCommandFlavor(option, out var args, out var jump))
                {
                    if (found)
                    {
                        // we'll pick the command with the longest arg path
                        if (commandArgs.Count == args.Count)
                        {
                            throw new ParserException("command ambigious", _stream.Current);
                        }

                        if (args.Count > commandArgs.Count)
                        {
                            foundCommand = option;
                            commandArgs = args;
                            foundJump = jump;
                        }
                    }
                    else
                    {
                        // this is the first find!
                        found = true;
                        foundCommand = option;
                        commandArgs = args;
                        foundJump = jump;
                    }
                }
            }

            if (!found)
            {
                throw new ParserException("No overload for method found", _stream.Current);
            }
            
            _stream.Restore(foundJump);
        }
        
        private void ParseCommandOverload(Token token, out CommandInfo command, out List<IExpressionNode> commandArgs)
        {
            if (!_commands.TryGetCommandDescriptor(token, out var possibleCommands))
            {
                throw new Exception("Parser exception! unknown command " + token.caseInsensitiveRaw);
            }

            possibleCommands = possibleCommands.ToList(); // make a copy!

            // parse the args!
            // var argExpressions = ParseCommandArgs(token, command);
            commandArgs = ParseAvailableArgs(token);

            // now, based on the args that EXIST, we need to boil down the possible commands...

            var actualCommands = new List<CommandInfo>();
            foreach (var commandOption in possibleCommands)
            {
                if (IsValidArgCollection(commandOption, commandArgs))
                {
                    actualCommands.Add(commandOption);
                }
            }
            
            
            if (actualCommands.Count == 0)
            {
                throw new ParserException("There was an error matching the args to the available command",
                    token);
            }

            if (actualCommands.Count > 1)
            {
                throw new ParserException(
                    "There are too many possible overloads for the method call, and it is ambigious",
                    token);
            }

            command = actualCommands.First();

        }


        private ParameterNode ParseParameterNode()
        {
            var typeMember = ParseTypeMember();
            return new ParameterNode(typeMember.name, typeMember.type);
        }

        private FunctionReturnStatement ParseExitFunction(Token endToken)
        {
            var expression = ParseWikiExpression();
            return new FunctionReturnStatement(endToken, expression);
        }
        
        private FunctionStatement ParseFunction(Token functionToken)
        {
            // parse the name

            var nameToken = _stream.Advance();
            if (nameToken.type != LexemType.VariableGeneral)
            {
                throw new ParserException("Exepcted to find valid function name", nameToken);
            }
            
            if (_stream.Advance().type != LexemType.ParenOpen)
            {
                throw new ParserException("Expected to find open paren", _stream.Current);
            }
            
            // now we need to parse a set of arguments....
            // TODO: 
            var parameters = new List<ParameterNode>();
            var looking = true;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        throw new ParserException("Hit end of file without a closing function parameter list", nextToken);
                    case LexemType.ArgSplitter:
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.ParenClose:
                        _stream.Advance();
                        looking = false;
                        break;
                    default:
                        var member = ParseParameterNode();
                        parameters.Add(member);
                        break; 
                }
            }
            
            if (_stream.Current.type != LexemType.ParenClose)
            {
                throw new ParserException("Expected to find open paren", _stream.Current);
            }

            // now we need to parse all the statements
            var statements = new List<IStatementNode>();
            looking = true;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        throw new ParserException("Hit end of file without a closing function statement", nextToken);
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.KeywordEndFunction:
                        _stream.Advance();
                        
                        // there may be an expression...
                        // ParseWikiExpression()
                        if (TryParseExpression(out var returnExpr))
                        {
                            statements.Add(new FunctionReturnStatement(nextToken, returnExpr));
                        }
                        
                        looking = false;
                        break;
                    default:
                        var member = ParseStatement();
                        statements.Add(member);
                        break; 
                }
            }
            

            return new FunctionStatement
            {
                statements = statements,
                parameters = parameters,
                name = nameToken.caseInsensitiveRaw,
                startToken = functionToken,
                endToken = _stream.Current
            };
        }

        private SwitchStatement ParseSwitchStatement(Token switchToken)
        {
            var expression = ParseWikiExpression();
            
            // now, there are a set of cases...
            var looking = true;
            DefaultCaseStatement defaultCase = null;
            List<CaseStatement> caseStatements = new List<CaseStatement>();
            while (looking)
            {
                switch (_stream.Peek.type)
                {
                    case LexemType.EOF:
                        throw new ParserException("Hit end of file without a closing select statement", switchToken);
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.KeywordCase:
                        var caseToken = _stream.Advance();

                        switch (_stream.Peek.type)
                        {
                            case LexemType.KeywordCaseDefault:
                                if (defaultCase != null)
                                {
                                    throw new ParserException("Select statement can only have 1 default case",
                                        defaultCase.StartToken);
                                }
                                defaultCase = ParseDefaultCase(caseToken);
                                break;
                            default:
                                var caseStatement = ParseCaseStatement(caseToken);
                                caseStatements.Add(caseStatement);
                                break;
                        }
                        
                        // parse a case statement!
                        break;
                    
                    case LexemType.KeywordEndSelect:
                        looking = false;
                        _stream.Advance(); // discard token
                        break;
                }
            }

            return new SwitchStatement
            {
                startToken = switchToken,
                endToken = _stream.Current,
                cases = caseStatements,
                defaultCase = defaultCase,
                expression = expression
            };

        }

        private CaseStatement ParseCaseStatement(Token caseToken)
        {
            var literals = ParseLiteralList(_stream.Advance());

            var statements = new List<IStatementNode>();
            var looking = true;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        throw new ParserException("Hit end of file without a closing case statement", caseToken);
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.KeywordEndCase:
                        _stream.Advance();
                        looking = false;
                        break;
                    case LexemType.KeywordEndSelect:
                        throw new ParserException("Must close all cases before ending select statement", caseToken);
                    default:
                        var member = ParseStatement();
                        statements.Add(member);
                        break; 
                }
            }

            return new CaseStatement
            {
                startToken = caseToken,
                endToken = _stream.Current,
                statements = statements,
                values = literals
            };
        }

        private DefaultCaseStatement ParseDefaultCase(Token caseToken)
        {
            _stream.Advance(); // discard the "default"
            
            
            var statements = new List<IStatementNode>();
            var looking = true;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        throw new ParserException("Hit end of file without a closing case statement", caseToken);
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.KeywordEndCase:
                        _stream.Advance();
                        looking = false;
                        break;
                    case LexemType.KeywordEndSelect:
                        throw new ParserException("Must close all cases before ending select statement", caseToken);
                    default:
                        var member = ParseStatement();
                        statements.Add(member);
                        break; 
                }
            }

            return new DefaultCaseStatement
            {
                startToken = caseToken,
                endToken = _stream.Current,
                statements = statements
            };
        }

        private ForStatement ParseForStatement(Token forToken)
        {
            var variable = ParseVariableReference();
            
            // next token must be an equal token.
            var equalToken = _stream.Advance();
            if (equalToken.type != LexemType.OpEqual)
            {
                throw new ParserException("Expected to find equal symbol", equalToken);
            }

            var startExpr = ParseWikiExpression();
            // next token must be a TO token
            var toToken = _stream.Advance();
            if (toToken.type != LexemType.KeywordTo)
            {
                throw new ParserException("Expected to find TO symbol", toToken);
            }

            var endExpr = ParseWikiExpression();
            // optionally, there may be a step token
            IExpressionNode stepExpr = null;

            switch (_stream.Peek.type)
            {
                case LexemType.KeywordStep:
                    // discard step token
                    _stream.Advance();
                    stepExpr = ParseWikiExpression();
                    break;
                default:
                    stepExpr = new LiteralIntExpression(endExpr.StartToken, 1);
                    break;
            }
            
            // now it is time to parse the statements!
            var statements = new List<IStatementNode>();
            var looking = true;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        throw new ParserException("Hit end of file without a closing NEXT statement", nextToken);
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.KeywordNext:
                        _stream.Advance();
                        looking = false;
                        break;
                    default:
                        var member = ParseStatement();
                        statements.Add(member);
                        break; 
                }
            }

            return new ForStatement(
                forToken, _stream.Current, variable, startExpr, endExpr, stepExpr, statements);
        }

        private DoLoopStatement ParseDoLoopStatement(Token doToken)
        {
            var statements = new List<IStatementNode>();
            var looking = true;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        throw new ParserException("Hit end of file without a closing loop statement", nextToken);
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.KeywordLoop:
                        _stream.Advance();
                        looking = false;
                        break;
                    default:
                        var member = ParseStatement();
                        statements.Add(member);
                        break; 
                }
            }

            return new DoLoopStatement(doToken, _stream.Current, statements);
        }

        
        private RepeatUntilStatement ParseRepeatUntil(Token whileToken)
        {
            var statements = new List<IStatementNode>();
            var looking = true;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        throw new ParserException("Hit end of file without a closing until statement", nextToken);
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.KeywordUntil:
                        _stream.Advance();
                        looking = false;
                        break;
                    default:
                        var member = ParseStatement();
                        statements.Add(member);
                        break; 
                }
            }
            var condition = ParseWikiExpression();

            return new RepeatUntilStatement
            {
                startToken = whileToken,
                endToken = _stream.Current,
                statements = statements,
                condition = condition
            };
        }

        
        private WhileStatement ParseWhileStatement(Token whileToken)
        {
            var condition = ParseWikiExpression();
            var statements = new List<IStatementNode>();
            var looking = true;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        throw new ParserException("Hit end of file without a closing endWhile statement", nextToken);
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.KeywordEndWhile:
                        _stream.Advance();
                        looking = false;
                        break;
                    default:
                        var member = ParseStatement();
                        statements.Add(member);
                        break; 
                }
            }

            return new WhileStatement
            {
                startToken = whileToken,
                endToken = _stream.Current,
                statements = statements,
                condition = condition
            };
        }
        
        private IfStatement ParseIfStatement(Token ifToken)
        {
            // the next term is an expression.
            var condition = ParseWikiExpression();
            
            // and then there is a split
            var next = _stream.Advance();
            var positiveStatements = new List<IStatementNode>();
            var negativeStatements = new List<IStatementNode>();
            var statements = positiveStatements;
            var looking = true;

            switch (next.type)
            {
                case LexemType.KeywordThen:
                    
                    // there is ONE statement...
                    // var statement = ParseStatement();
                    // return new IfStatement(ifToken, _stream.Current, condition, new List<IStatementNode> { statement });
                    
                    while (looking)
                    {
                        var nextToken = _stream.Peek;
                        switch (nextToken.type)
                        {
                            case LexemType.EndStatement when nextToken.caseInsensitiveRaw == ":":
                                _stream.Advance();
                                break;
                            case LexemType.EndStatement:
                            case LexemType.EOF:
                                looking = false;
                                break;
                                // throw new ParserException("Hit end of file without a closing type statement", nextToken);
                            case LexemType.KeywordEndIf:
                                _stream.Advance();
                                looking = false;
                                break;
                            case LexemType.KeywordElse:
                                _stream.Advance(); // skip else block
                                statements = negativeStatements;
                                break;
                            default:
                                var member = ParseStatement(consumeEndOfStatement: false);
                                statements.Add(member);
                                break; 
                        }
                    }

                    return new IfStatement(ifToken, _stream.Current, condition, positiveStatements, negativeStatements);
                
                default:

                    while (looking)
                    {
                        var nextToken = _stream.Peek;
                        switch (nextToken.type)
                        {
                            case LexemType.EOF:
                                throw new ParserException("Hit end of file without a closing type statement", nextToken);
                            case LexemType.EndStatement:
                                _stream.Advance();
                                break;
                            case LexemType.KeywordEndIf:
                                _stream.Advance();
                                looking = false;
                                break;
                            case LexemType.KeywordElse:
                                _stream.Advance(); // skip else block
                                statements = negativeStatements;
                                break;
                            default:
                                var member = ParseStatement();
                                statements.Add(member);
                                break; 
                        }
                    }

                    return new IfStatement(ifToken, _stream.Current, condition, positiveStatements, negativeStatements);
                    
            }

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
                case LexemType.KeywordAnd:
                case LexemType.KeywordOr:
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
                case LexemType.OpGte:
                case LexemType.OpLte:
                case LexemType.OpNotEqual:
                case LexemType.OpPower:
                case LexemType.OpMod:
                case LexemType.KeywordAnd:
                case LexemType.KeywordOr:
                    return true;
                default:
                    return false;
            }
        }

        public bool TryParseExpression(out IExpressionNode expr)
        {
            expr = null;
            if (!TryParseWikiTerm(out var term))
            {
                return false;
            }

            expr = ParseWikiExpression(term, 0);
            return true;
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

        private ILiteralNode ParseLiteral(Token token)
        {
            switch (token.type)
            {
                case LexemType.LiteralInt:
                    return new LiteralIntExpression(token);
                case LexemType.LiteralReal:
                    return new LiteralRealExpression(token);
                case LexemType.LiteralString:
                    return new LiteralStringExpression(token);
                default:
                    throw new ParserException("Expected a literal", token);
            }
        }

        private List<ILiteralNode> ParseLiteralList(Token start)
        {
            var literals = new List<ILiteralNode>();
            literals.Add(ParseLiteral(start));
            var looking = true;
            while (looking)
            {

                switch (_stream.Peek.type)
                {
                    case LexemType.LiteralInt:
                    case LexemType.LiteralReal:
                    case LexemType.LiteralString:
                        literals.Add(ParseLiteral(_stream.Advance()));
                        break;
                    case LexemType.ArgSplitter:
                        _stream.Advance(); // discard ,
                        break;
                    case LexemType.EndStatement:
                        _stream.Advance(); // discard eos
                        break;
                    default:
                        looking = false;
                        break;
                }
            }


            return literals;
        }

        private IExpressionNode ParseWikiTerm()
        {
            var start = _stream.Peek;
            if (!TryParseWikiTerm(out var expr))
            {
                throw new ParserException("Expected to find an expression term", start);
            }
            
            return expr;
        }
        
        private bool TryParseWikiTerm(out IExpressionNode outputExpression)
        {
            var token = _stream.Peek;
            switch (token.type)
            {
                
                case LexemType.CommandWord:
                    _stream.Advance();

                    // ParseCommandOverload(token, out var command, out var argExpressions);
                    ParseCommandOverload2(token, out var command, out var argExpressions);
                    
                    // if (!_commands.TryGetCommandDescriptor(token, out var command))
                    // {
                    //     throw new Exception("Parser exception! unknown command " + token.caseInsensitiveRaw);
                    // }
                    //
                    // // parse the args!
                    // var argExpressions = ParseCommandArgs(token, command);
                    outputExpression = new CommandExpression()
                    {
                        startToken = token,
                        endToken = _stream.Current,
                        command = command,
                        args = argExpressions
                    };
                    
                    break;
                case LexemType.ParenOpen:
                    _stream.Advance();

                    var expr = ParseWikiExpression();
                    var closeToken = _stream.Advance(); // move past closing...

                    if (closeToken.type != LexemType.ParenClose)
                    {
                        throw new ParserException("expected closing paren", closeToken);
                    }

                    outputExpression = expr;
                    
                    break;
                
                case LexemType.VariableReal:
                case LexemType.VariableString:
                case LexemType.VariableGeneral:
                    _stream.Advance();

                    outputExpression = ParseVariableReference(token);
                    break;
                case LexemType.LiteralInt:
                case LexemType.LiteralReal:
                case LexemType.LiteralString:
                    _stream.Advance();

                    outputExpression = ParseLiteral(token);
                    break;
                case LexemType.OpMultiply:
                    _stream.Advance();

                    var deRefExpr = ParseVariableReference();
                    outputExpression = new DereferenceExpression(deRefExpr, token);
                    break;
                case LexemType.OpMinus:
                    _stream.Advance();

                    var negateExpr = ParseWikiTerm();
                    outputExpression = new UnaryOperationExpression(UnaryOperationType.Negate, negateExpr, token, _stream.Current);
                    break;
                case LexemType.KeywordNot:
                    _stream.Advance();

                    var peek = _stream.Peek;
                    var notExpr = ParseWikiExpression();
                    if (peek.type != LexemType.ParenOpen)
                    {
                        // if the not expression is a binary op, and it has a right side with a higher op...
                        switch (notExpr)
                        {
                            case BinaryOperandExpression binOp:
                                if (binOp.operationType == OperationType.And || binOp.operationType == OperationType.Or)
                                {
                                    // ah, then we should flip-arro
                                    var oldLhs = binOp.lhs;
                                    binOp.lhs = new UnaryOperationExpression(UnaryOperationType.Not, oldLhs, token,
                                        _stream.Current);
                                    outputExpression = binOp;
                                    return true;
                                }

                                break;
                        }
                    }

                    outputExpression = new UnaryOperationExpression(UnaryOperationType.Not, notExpr, token, _stream.Current);
                    break;
                default:
                    outputExpression = null;
                    break;
                    // throw new ParserException("Cannot match single, " + token.type, token);
            }

            return outputExpression != null;
        }
        
    }
}
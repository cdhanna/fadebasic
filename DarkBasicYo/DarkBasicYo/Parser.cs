using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using DarkBasicYo.Ast;
using DarkBasicYo.Ast.Visitors;
using DarkBasicYo.Virtual;
using TypeInfo = DarkBasicYo.Ast.TypeInfo;

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

    public class Symbol
    {
        public string text;
        public TypeInfo typeInfo;
        public IAstNode source;

        public Symbol()
        {
            
        }
    }

    public class SymbolTable : Dictionary<string, Symbol>
    {
        public void Add(Symbol symbol)
        {
            Add(symbol.text, symbol);
        }
        public new void Add(string variableName, Symbol symbol)
        {
            if (variableName == "_") return;
            base.Add(variableName, symbol);
        }
    }
    
    public class Scope
    {

        public HashSet<string> labels = new HashSet<string>();
        public Dictionary<string, SymbolTable> typeNameToTypeMembers = new Dictionary<string, SymbolTable>();
        public SymbolTable globalVariables = new SymbolTable();
        public Stack<SymbolTable> localVariables = new Stack<SymbolTable>();

        public Dictionary<string, Symbol> functionTable = new Dictionary<string, Symbol>();
        // public Stack<List<Symbol>> localVariables = new Stack<List<Symbol>>();

        public Scope()
        {
            localVariables.Push(new SymbolTable());
        }

        public static Scope CreateStructScope(Scope scope, SymbolTable typeSymbols)
        {
            var typeScope = new Scope();
            typeScope.typeNameToTypeMembers = scope.typeNameToTypeMembers;

            foreach (var kvp in typeSymbols)
            {
                typeScope.localVariables.Peek().Add(kvp.Key, kvp.Value);
            }

            return typeScope;
            // typeScope.AddDeclaration();
        }
        
        public void BeginFunction(FunctionStatement function)
        {
            var parameters = function.parameters;
            var table = new SymbolTable();
            localVariables.Push(table);

            if (functionTable.TryGetValue(function.name, out var existing))
            {
                function.Errors.Add(new ParseError(function.nameToken, ErrorCodes.FunctionAlreadyDeclared)); 
            }
            else
            {
                functionTable.Add(function.name, new Symbol
                {
                    text = function.name, 
                    typeInfo = TypeInfo.Void, // TODO: it would be cool if we could get the exitfunction and endfunction return values...
                    source = function
                });
            }

            foreach (var parameter in parameters)
            {
                var symbol = new Symbol
                {
                    text = parameter.variable.variableName,
                    typeInfo = TypeInfo.FromVariableType(parameter.type.variableType),
                    source = parameter
                };
                if (parameter.type is StructTypeReferenceNode typeRef)
                {
                    symbol.typeInfo = new TypeInfo
                    {
                        structName = typeRef.variableNode.variableName,
                        type = VariableType.Struct
                    };
                }
                table.Add(parameter.variable.variableName, symbol);
            }
        }

        public void EndFunction()
        {
            localVariables.Pop();
        }

        public void AddLabel(LabelDeclarationNode labelDecl)
        {
            labels.Add(labelDecl.label);
        }

        public void AddAssignment(AssignmentStatement assignment)
        {

            /*
             * if there isn't a declr yet, then the compiler will implicitly create one based off the implied type.
             * So in this step, we just need to record that the RHS symbol exists in the variableRef case.
             *
             * In the array/struct case, the variable MUST already exist.
             *
             *
             * This would also be a reasonable time to get the type of the RHS
             */
            switch (assignment.variable)
            {
                case VariableRefNode variableRef: // a = 3
                    // declr is optional...
                    if (TryGetSymbol(variableRef.variableName, out var existingSymbol))
                    {
                        // TODO: we could check for type consistency here?
                    }
                    else // no symbol exists, so this is a defacto local variable
                    {
                        var locals = GetVariables(DeclarationScopeType.Local);
                        locals.Add(variableRef.variableName, new Symbol
                        {
                            text = variableRef.variableName,
                            typeInfo = TypeInfo.FromVariableType(variableRef.DefaultTypeByName),
                            source = variableRef
                        });
                    }
                    break;
                case ArrayIndexReference indexRef: // a(1,2) = 1
                    // it isn't possible to assign an array without declaring it first- 
                    //  which means no new scopes need to be added.
                    break;
                case DeReference deRef: // *x = 3
                    // it isn't possible to de-ref a vairable without declaring it first- 
                    //  which means no new scopes need to be added.
                    break;
                case StructFieldReference structRef: // a.x.y = 1
                    // it isn't possible to assign to a struct field without declaring it first- 
                    //  which means no new scopes need to be added.
                    // but on the other hand, we do need to validate that the variable exists!

                    // switch (structRef.left)
                    // {
                    //     case VariableRefNode leftVariable:
                    //         if (!TryGetSymbol(leftVariable.variableName, out var leftSymbol))
                    //         {
                    //             
                    //         }
                    //         break;
                    //     default:
                    //         throw new NotImplementedException("I don't know how to handle this yet. jjsa");
                    // }
                    break;
            }
            
            // if (assignment.variable is VariableRefNode variableRef)
            // {
            //     // an assignment defaults to a local decl.
            //
            //     // variableRef.DefaultTypeByName
            //     var symbol = new Symbol
            //     {
            //         text = variableRef.variableName,
            //         // typeInfo = 
            //     };
            //     
            //     localVariables.Peek().Add(variableRef.variableName);
            // }
        }
        
        public void AddDeclaration(DeclarationStatement declStatement)
        {
            var table = GetVariables(declStatement.scopeType);
            if (table.ContainsKey(declStatement.variable))
            {
                // this is an error; we cannot declare a variable twice in the same scope.
                declStatement.Errors.Add(new ParseError(declStatement.StartToken, ErrorCodes.SymbolAlreadyDeclared));
                
                // don't do anything with this.
                return;
            }

            switch (declStatement.type)
            {
                case TypeReferenceNode typeReference:
                    table.Add(declStatement.variable, new Symbol
                    {
                        text = declStatement.variable,
                        typeInfo = TypeInfo.FromVariableType(typeReference.variableType, 
                            rank: declStatement.ranks?.Length ?? 0, 
                            structName: null),
                        source = declStatement
                    });
                    break;
                case StructTypeReferenceNode structTypeReference:
                    table.Add(declStatement.variable, new Symbol
                    {
                        text = declStatement.variable,
                        typeInfo = TypeInfo.FromVariableType(structTypeReference.variableType, 
                            rank: declStatement.ranks?.Length ?? 0, 
                            structName: structTypeReference.variableNode.variableName),
                        source = declStatement
                    });
                    break;
            }
            
        }

        SymbolTable GetVariables(DeclarationScopeType scopeType)
        {
            if (scopeType == DeclarationScopeType.Global)
            {
                return globalVariables;
            }
            else
            {
                return localVariables.Peek();
            }
        }

        public bool TryGetType(string typeName, out SymbolTable typeSymbols)
        {
            return typeNameToTypeMembers.TryGetValue(typeName, out typeSymbols);
        }

        public bool TryGetSymbol(string variableName, out Symbol symbol)
        {
            if (GetVariables(DeclarationScopeType.Local).TryGetValue(variableName, out symbol))
            {
                return true;
            } else if (GetVariables(DeclarationScopeType.Global).TryGetValue(variableName, out symbol))
            {
                return true;
            } else
            {
                return false;
            }
        }


        public void AddVariable(VariableRefNode variable)
        {
            localVariables.Peek().Add(variable.variableName, new Symbol
            {
                text = variable.variableName, 
                typeInfo = TypeInfo.FromVariableType(variable.DefaultTypeByName),
                source = variable
            });
        }

        public bool TryAddVariable(VariableRefNode variable)
        {
            var symbolTable = localVariables.Peek();
            var symbol = new Symbol
            {
                text = variable.variableName,
                typeInfo = TypeInfo.FromVariableType(variable.DefaultTypeByName),
                source = variable
            };
            if (symbolTable.TryGetValue(variable.variableName, out var existing))
            {
                return false; // TODO: maybe validate?
            }
            else
            {
                symbolTable.Add(variable.variableName, symbol);
                return true;
            }
        }

        public void AddType(TypeDefinitionStatement type)
        {
            var members = typeNameToTypeMembers[type.name.variableName] = new SymbolTable();
            foreach (var member in type.declarations)
            {
                var symbol = new Symbol
                {
                    text = member.name.variableName,
                    source = member
                };
                switch (member.type)
                {
                    // Note: it isn't possible to define an array inside a struct, so we don't need to worry about that.
                    case TypeReferenceNode typeRefNode:
                        symbol.typeInfo = TypeInfo.FromVariableType(typeRefNode.variableType);
                        break;
                    case StructTypeReferenceNode structRefNode:
                        symbol.typeInfo = TypeInfo.FromVariableType(VariableType.Struct, structName: structRefNode.variableNode.variableName);
                        // throw new NotImplementedException();
                        break;
                }

                if (members.ContainsKey(symbol.text))
                {
                    member.Errors.Add(new ParseError(member, ErrorCodes.SymbolAlreadyDeclared));
                }
                else
                {
                    members.Add(symbol);
                }
            }
        }

        public bool TryGetLabel(string label)
        {
            return labels.Contains(label);
        }

        public void AddCommand(CommandExpression commandExpr) =>
            AddCommand(commandExpr.command, commandExpr.args, commandExpr.argMap);
        
        public void AddCommand(CommandInfo command, List<IExpressionNode> args, List<int> argMap)
        {

            for (var i = 0; i < args.Count; i++)
            {
                var argExpr = args[i];
                var argDesc = command.args[argMap[i]];
                if (argDesc.isRef)
                {
                    if (argExpr is VariableRefNode variableRefNode)
                    {
                        TryAddVariable(variableRefNode);
                    }
                }
            }

            foreach (var expr in args)
            {
                switch (expr)
                {
                    case CommandExpression commandExpr:
                        // recursive call could explode given highly nested call stack. 
                        AddCommand(commandExpr.command, commandExpr.args, commandExpr.argMap);
                        break;
                }
            }
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

        public List<LexerError> GetLexingErrors() => _stream.Errors;

        public ProgramNode ParseProgram()
        {
            var program = new ProgramNode(_stream.Current);

            while (!_stream.IsEof)
            {
                var statement = ParseStatement();
                switch (statement)
                {
                    case FunctionStatement functionStatement:
                        program.functions.Add(functionStatement);
                        break;
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
            
            // program.AddTypeInfo();
            program.AddScopeRelatedErrors();
            
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
                    var asToken = _stream.Peek;
                    ParseError error = null;
                    if (asToken.type != LexemType.KeywordAs)
                    {
                        error = new ParseError(asToken, ErrorCodes.ScopedDeclarationExpectedAs);
                    }
                    else
                    {
                        _stream.Advance(); // skip As token
                    }
                    
                    var typeReference = ParseTypeReference();
                    if (error != null)
                    {
                        typeReference.Errors.Insert(0, error);
                    }
                    var declStatement = new DeclarationStatement(scopeToken, new VariableRefNode(next), typeReference);
                    
                    return declStatement;
                case LexemType.KeywordDeclareArray:
                    var decl = ParseDimStatement(next);
                    var arrayDeclStatement = new DeclarationStatement(scopeToken, decl);
                    return arrayDeclStatement;
                default:
                    var patchToken = _stream.CreatePatchToken(LexemType.VariableGeneral, "_")[0];

                    _stream.AdvanceUntil(LexemType.EndStatement);
                    var fake = new DeclarationStatement(scopeToken, new VariableRefNode(patchToken),
                        new TypeReferenceNode(VariableType.Integer, patchToken));
                    fake.Errors.Add(new ParseError(patchToken, ErrorCodes.ScopedDeclarationInvalid));
                    return fake;
                    
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
            var errors = new List<ParseError>();
            switch (next.type)
            {
                case LexemType.VariableString:
                case LexemType.VariableReal:
                case LexemType.VariableGeneral:
                    // dim x
                    var openParenToken = _stream.Peek;
                    if (openParenToken.type != LexemType.ParenOpen)
                    {
                        errors.Add(new ParseError(openParenToken, ErrorCodes.ArrayDeclarationMissingOpenParen));
                    }
                    else
                    {
                        _stream.Advance(); // skip the (
                    }
                    
                    var rankExpressions = new List<IExpressionNode>();
                    var rankIndex = -1;
                    while (true)
                    // for (var rankIndex = 0; rankIndex < 5; rankIndex++) // 5 is the magic "max dimension"
                    {
                        rankIndex++;
                        var curr = _stream.Current;
                        if (!TryParseExpression(out var rankExpression, out var recovery))
                        {
                            if (rankIndex == 0)
                            {
                                errors.Add(new ParseError(curr, ErrorCodes.ArrayDeclarationRequiresSize));
                            }
                            else
                            {
                                // errors.Add();
                            }
                            _stream.Patch(recovery.index, recovery.correctiveTokens);

                            if (!TryParseExpression(out rankExpression, out _))
                            {
                                throw new Exception("Failed to patch stream");
                            }
                        }
                        // var rankExpression = ParseWikiExpression();
                        rankExpressions.Add(rankExpression);
                        var closeFound = false;
                        var looking = true;
                        while (looking)
                        {
                            var closeOrComma = _stream.Advance();
                            switch (closeOrComma.type)
                            {
                                case LexemType.ParenClose:
                                    closeFound = true;
                                    looking = false;
                                    break;
                                case LexemType.ArgSplitter:
                                    if (rankIndex + 1 == 5)
                                    {
                                        errors.Add(new ParseError(closeOrComma, ErrorCodes.ArrayDeclarationSizeLimit));
                                    }
                                    looking = false;

                                    break;
                                case LexemType.EndStatement:
                                case LexemType.EOF:
                                    errors.Add(new ParseError(closeOrComma,
                                        ErrorCodes.ArrayDeclarationMissingCloseParen));
                                    closeFound = true;
                                    looking = false;

                                    break;
                                default:
                                    errors.Add(new ParseError(closeOrComma,
                                        ErrorCodes.ArrayDeclarationInvalidSizeExpression));
                                    break;
                            }
                        }

                        if (closeFound)
                        {
                            break;
                        }
                    }

                    // so far, we have dim x(n)
                    // next, we _could_ have an AS, or it could be an end-of-statement

                    // if (_stream.Peek?.type == LexemType.KeywordAs)
                    // {
                    //   
                    // }

                    switch (_stream.Peek?.type)
                    {
                        case LexemType.KeywordAs:
                            _stream.Advance(); // discard the "as"
                            var typeReference = ParseTypeReference();
                            var sub = new DeclarationStatement(dimToken, new VariableRefNode(next), typeReference,
                                rankExpressions.ToArray());
                            sub.Errors.AddRange(errors);
                            return sub;
                        default:
                            break;
                        // case LexemType.EndStatement:
                        // case LexemType.EOF:
                        //     break;
                        // default:
                        //     // error case?
                        //     errors.Add(new ParseError(_stream.Current, ErrorCodes.ArrayDeclarationInvalidSizeExpression));
                        //
                        //     _stream.AdvanceUntil(LexemType.EndStatement);
                        //     break;
                    }
                    
                    var statement = new DeclarationStatement(
                        dimToken, 
                        new VariableRefNode(next), 
                        rankExpressions.ToArray());
                    statement.Errors = errors;
                    return statement;
                    break;
                default:
                    var patchToken = _stream.CreatePatchToken(LexemType.VariableGeneral, "_")[0];
                    var fake = new DeclarationStatement(dimToken, new VariableRefNode(patchToken),
                        new IExpressionNode[1]);
                    fake.Errors.Add(new ParseError(patchToken, ErrorCodes.ArrayDeclarationInvalid));
                    return fake;
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
                            ParseError error = null;
                            // var statements = new List<IStatementNode>();
                            var looking = true;
                            while (looking)
                            {
                                var nextToken = _stream.Peek;
                                switch (nextToken.type)
                                {
                                    case LexemType.EOF:

                                        error = new ParseError(next,
                                            ErrorCodes.VariableIndexMissingCloseParen);
                                        // _stream.Patch();
                                        // _stream.Advance()
                                        
                                        // normally we would pretend this is a ) to signal the end,
                                        //  but since this was triggered by EOF, there is no point looking forward anyway.
                                        looking = false; 
                                        break;
                                        
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
                            if (error != null)
                            {
                                indexValue.Errors.Add(error);
                            }
                            
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
                    
                    // to correct the error, return a fake variable reference with a logged error
                    var fakeRef = new VariableRefNode(_stream.CreatePatchToken(LexemType.VariableGeneral, "_", -2)[0]);
                    fakeRef.Errors.Add(new ParseError(fakeRef.startToken, ErrorCodes.VariableReferenceMissing));
                    return fakeRef;
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
                                var labelDecl = new LabelDeclarationNode(token, secondToken);
                                return labelDecl;
                            case LexemType.EndStatement:
                                return new ExpressionStatement(reference); // TODO: eh?

                            case LexemType.OpEqual:
                                var expr = ParseWikiExpression();
                                
                                // we actually need to emit a declaration node and an assignment. 
                                var assignment = new AssignmentStatement
                                {
                                    startToken = token,
                                    endToken = _stream.Current,
                                    variable = reference,
                                    expression = expr
                                };
                                return assignment;
                            case LexemType.KeywordAs:
                                var type = ParseTypeReference();
                                // TODO: if the type is an array, then we should make the scope global by default.
                                var scopeType = DeclarationScopeType.Local;
                                var decl = new DeclarationStatement
                                {
                                    startToken = token,
                                    endToken = _stream.Current,
                                    type = type,
                                    scopeType = scopeType,
                                    variable = token.caseInsensitiveRaw
                                };
                                return decl;
                            default:
                                
                                // this is an error case, and the most general solution is to skip ahead to an end-statement and start anew. 
                                _stream.AdvanceUntil(LexemType.EndStatement);
                                var statement = new NoOpStatement();
                                statement.Errors.Add(new ParseError(secondToken, ErrorCodes.AmbiguousDeclarationOrAssignment));
                                return statement;
                        }

                        break;

                    case LexemType.CommandWord:
                        ParseCommandOverload2(token, out var command, out var commandArgs, out var argMap, out var errors);
                        var commandStatement = new CommandStatement
                        {
                            startToken = token,
                            endToken = _stream.Current,
                            command = command,
                            args = commandArgs,
                            argMap = argMap,
                        };
                        commandStatement.Errors.AddRange(errors);
                        return commandStatement;

                    case LexemType.KeywordType:
                        return ParseTypeDefinition(token);
                        
                        break;
                    default:
                        _stream.AdvanceUntil(LexemType.EndStatement);
                        return new NoOpStatement
                        {
                            startToken = token,
                            endToken = _stream.Current,
                            Errors = new List<ParseError>()
                            {
                                new ParseError(token, ErrorCodes.UnknownStatement)
                            }
                        };
                    
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

        private bool TryParseCommandFlavor(CommandInfo command, out List<IExpressionNode> args, out List<int> argExprMap, out int tokenJump)
        {
            var start = _stream.Save();
            var startToken = _stream.Current;
            tokenJump = -1;
            args = new List<IExpressionNode>();
            argExprMap = new List<int>(); // this is a parallel array to the args list; and the values represent the index of the CommandArgInfo that the arg expression is part of.

            var requiresParens = command.returnType != TypeCodes.VOID;
            
            
            bool isOpenParen = _stream.Peek.type == LexemType.ParenOpen;

            if (requiresParens)
            {
                if (!isOpenParen)
                {
                    return false;
                }

                _stream.Advance(); // discard the open paren!
            }
            
            IExpressionNode firstExpr = null;
            if (isOpenParen)
            {
                // there may be an expression here... or this could just be opening the function
                if (!TryParseExpression(out firstExpr))
                {
                    // if we couldn't parse the expression, then these parens are just for opening the function
                    if (!requiresParens)
                    {
                        _stream.Advance();
                    }

                    isOpenParen = true;
                }
                else
                {
                    isOpenParen = false;
                }
                // discard
                // add (3+2)*2, 4
                // add (3+2*2,4)
            }

            bool TryGetNextExpr(out IExpressionNode expr)
            {
                if (firstExpr != null)
                {
                    expr = firstExpr;
                    firstExpr = null;
                    return true;
                }

                return TryParseExpression(out expr);
            }

            bool EnsureCloseParen()
            {
                if (requiresParens || isOpenParen)
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
                
                
                
                if (!TryGetNextExpr(out var expr))
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
                    argExprMap.Add(i);

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
        
        private void ParseCommandOverload2(Token token, out CommandInfo foundCommand, out List<IExpressionNode> commandArgs, out List<int> argMap, out List<ParseError> errors)
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
            argMap = new List<int>();
            errors = new List<ParseError>();
            int foundJump = -1;
            for (var i = 0 ; i < possibleCommands.Count; i ++)
            {
                var option = possibleCommands[i];
                if (TryParseCommandFlavor(option, out var args, out var foundArgMap, out var jump))
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
                            argMap = foundArgMap;
                            foundJump = jump;
                        }
                    }
                    else
                    {
                        // this is the first find!
                        found = true;
                        foundCommand = option;
                        commandArgs = args;
                        argMap = foundArgMap;
                        foundJump = jump;
                    }
                }
            }

            if (!found)
            {
                /*
                 * the command WAS a valid token parse, but we couldn't find any overload.
                 * that means we can pick the first option and inject it into the parse tree with errors
                 * but it is tricky how to get the jump-ahead token index, so that we do not parse them incorrectly later...
                 * 
                 */

                foundCommand = possibleCommands[0];
                commandArgs = new List<IExpressionNode>(); // don't actually need to fill these...
                argMap = new List<int>();
                errors.Add(new ParseError(token, ErrorCodes.CommandNoOverloadFound));
                bool isOpenParen = _stream.Peek.type == LexemType.ParenOpen;
                _stream.Advance(); // discard open paren...

                while (!_stream.IsEof)
                {
                    var next = _stream.Peek;
                    var isDone = false;
                    switch (next.type)
                    {
                        case LexemType.ParenClose when isOpenParen:
                            isDone = true;
                            _stream.Advance(); // discard close paren
                            break;
                        case LexemType.EOF:
                        case LexemType.EndStatement:
                            isDone = true;
                            _stream.Advance(); // discard 
                            // the expression line is done parsing... 
                            break;
                        case LexemType.ArgSplitter:
                            // parse the next expression
                            _stream.Advance(); // discard

                            break;
                        default:
                            if (!TryParseExpression(out var arg))
                            {
                                arg = new LiteralIntExpression(_stream.CreatePatchToken(LexemType.LiteralInt, "0")[0]);
                            }
                            commandArgs.Add(arg);
                            argMap.Add(0);
                            break;
                    }

                    if (isDone)
                    {
                        break;
                    }
                }

                return;
                // throw new ParserException("No overload for method found", _stream.Current);
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
            var node = new ParameterNode(typeMember.name, typeMember.type);
            node.Errors.AddRange(typeMember.Errors);
            return node;
        }

        private FunctionReturnStatement ParseExitFunction(Token endToken)
        {
            var expression = ParseWikiExpression();
            return new FunctionReturnStatement(endToken, expression);
        }
        
        private FunctionStatement ParseFunction(Token functionToken)
        {
            var errors = new List<ParseError>();

            // parse the name
            var nameToken = _stream.Peek;
            if (nameToken.type != LexemType.VariableGeneral)
            {
                nameToken = _stream.CreatePatchToken(LexemType.VariableGeneral, "_")[0];
                errors.Add(new ParseError(functionToken, ErrorCodes.FunctionMissingName));
            }
            else
            {
                _stream.Advance();
            }

            if (_stream.Peek.type != LexemType.ParenOpen)
            {
                errors.Add(new ParseError(functionToken, ErrorCodes.FunctionMissingOpenParen));
            }
            else
            {
                _stream.Advance(); // consume open paren
            }
            
            // now we need to parse a set of arguments....
            var parameters = new List<ParameterNode>();
            var looking = true;
            Token peekToken = _stream.Current;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        looking = false;
                        break;
                    case LexemType.ArgSplitter:
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.KeywordEndFunction:
                        looking = false;
                        break;
                        
                    case LexemType.ParenClose:
                        _stream.Advance();
                        looking = false;
                        peekToken = nextToken;
                        break;
                    default:
                        var member = ParseParameterNode();
                        parameters.Add(member);
                        peekToken = nextToken;
                        // errors.Add(new ParseError(nextToken, ErrorCodes.FunctionMissingCloseParen));
                        // looking = false; // let the if statement for closing paren catch this...
                        break; 
                }

                if (looking)
                {
                }
            }
            
            if (_stream.Current.type != LexemType.ParenClose)
            {
                // we can safely ignore the lack of a close and just continue...
                errors.Add(new ParseError(peekToken, ErrorCodes.FunctionMissingCloseParen));
                
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
                        errors.Add(new ParseError(functionToken, ErrorCodes.FunctionMissingEndFunction));
                        looking = false;
                        break;
                        
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
                    case LexemType.KeywordFunction:
                        var illegalFunctionMember = ParseStatement();
                        statements.Add(illegalFunctionMember);
                        illegalFunctionMember.Errors.Add(new ParseError(nextToken, ErrorCodes.FunctionDefinedInsideFunction));
                        break;
                    default:
                        var member = ParseStatement();
                        statements.Add(member);
                        break; 
                }
            }


            return new FunctionStatement
            {
                nameToken = nameToken,
                Errors = errors,
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
            var errors = new List<ParseError>();
            while (looking)
            {
                switch (_stream.Peek.type)
                {
                    case LexemType.EOF:
                        errors.Add(new ParseError(switchToken, ErrorCodes.SelectStatementMissingEndSelect));
                        looking = false;
                        break;
                        
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
                                    errors.Add(new ParseError(_stream.Peek, ErrorCodes.MultipleDefaultCasesFound));
                                    // even though this default case is bad, we'd like to know if there are parse errors in the inner statements.
                                    var badCaseStatement = ParseDefaultCase(caseToken);
                                    var fakeCaseStatement = new CaseStatement
                                    {
                                        values = new List<ILiteralNode>(),
                                        statements = badCaseStatement.statements
                                    };
                                    caseStatements.Add(fakeCaseStatement);
                                    break;
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
                    default:
                        // invalid token was given to us
                        errors.Add(new ParseError(_stream.Peek, ErrorCodes.SelectStatementUnknownCase));
                        _stream.Advance(); // discard token
                        break;
                }
            }

            var statement = new SwitchStatement
            {
                startToken = switchToken,
                endToken = _stream.Current,
                cases = caseStatements,
                defaultCase = defaultCase,
                expression = expression,
                Errors = errors
            };
            return statement;

        }

        private CaseStatement ParseCaseStatement(Token caseToken)
        {
            
            var literals = ParseLiteralList(_stream.Advance());

            var statements = new List<IStatementNode>();
            var looking = true;
            var errors = new List<ParseError>();
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        errors.Add(new ParseError(caseToken, ErrorCodes.CaseStatementMissingEndCase));
                        looking = false;
                        break;
                        
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.KeywordEndCase:
                        _stream.Advance();
                        looking = false;
                        break;
                    case LexemType.KeywordEndSelect:
                        errors.Add(new ParseError(caseToken, ErrorCodes.CaseStatementMissingEndCase));
                        looking = false;
                        break;
                        
                    default:
                        var member = ParseStatement();
                        statements.Add(member);
                        break; 
                }
            }

            return new CaseStatement
            {
                Errors = errors,
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
            var errors = new List<ParseError>();
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        errors.Add(new ParseError(caseToken, ErrorCodes.CaseStatementMissingEndCase));
                        looking = false;
                        break;
                        
                    case LexemType.EndStatement:
                        _stream.Advance();
                        break;
                    case LexemType.KeywordEndCase:
                        _stream.Advance();
                        looking = false;
                        break;
                    case LexemType.KeywordEndSelect:
                        errors.Add(new ParseError(caseToken, ErrorCodes.CaseStatementMissingEndCase));
                        looking = false;
                        break;
                        
                    default:
                        var member = ParseStatement();
                        statements.Add(member);
                        break; 
                }
            }

            return new DefaultCaseStatement
            {
                Errors = errors,
                startToken = caseToken,
                endToken = _stream.Current,
                statements = statements
            };
        }

        private ForStatement ParseForStatement(Token forToken)
        {
            var variable = ParseVariableReference();
            var errors = new List<ParseError>();
            
            // next token must be an equal token.
            if (_stream.IsEof)
            {
                var fakeValue = new LiteralIntExpression(_stream.CreatePatchToken(LexemType.LiteralInt, "0")[0]);
                var brokenFor = new ForStatement(forToken, forToken, variable, fakeValue, fakeValue, fakeValue,
                    new List<IStatementNode>());
                
                brokenFor.Errors.Add(new ParseError(forToken, ErrorCodes.ForStatementMissingOpening));
                return brokenFor;
            }
            var equalCheckpoint = _stream.Save();
            var equalToken = _stream.Advance();
            if (equalToken.type != LexemType.OpEqual)
            {
                // just pretend there _was_ an equal... by skipping back a token.
                _stream.Restore(equalCheckpoint);
                errors.Add(new ParseError(equalToken, ErrorCodes.ForStatementMissingOpening));
                
            }

            var startExpr = ParseWikiExpression();
            // next token must be a TO token
            var toCheckpoint = _stream.Save();
            var toToken = _stream.Advance();
            if (toToken.type != LexemType.KeywordTo)
            {
                _stream.Restore(toCheckpoint);
                errors.Add(new ParseError(toToken, ErrorCodes.ForStatementMissingTo));
                
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
                        errors.Add(new ParseError(forToken, ErrorCodes.ForStatementMissingNext));
                        looking = false;
                        break;
                        
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

            var forStatement = new ForStatement(
                forToken, _stream.Current, variable, startExpr, endExpr, stepExpr, statements);
            forStatement.Errors = errors;
            return forStatement;
        }

        private DoLoopStatement ParseDoLoopStatement(Token doToken)
        {
            var statements = new List<IStatementNode>();
            var looking = true;
            ParseError error = null;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        error = new ParseError(doToken, ErrorCodes.DoStatementMissingLoop);
                        looking = false;
                        break;
                        
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

            var doStatement = new DoLoopStatement(doToken, _stream.Current, statements);
            if (error != null)
            {
                doStatement.Errors.Add(error);
            }

            return doStatement;
        }

        
        private RepeatUntilStatement ParseRepeatUntil(Token repeatToken)
        {
            var statements = new List<IStatementNode>();
            var looking = true;
            ParseError error = null;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        error = new ParseError(repeatToken, ErrorCodes.RepeatStatementMissingUntil);
                        looking = false;
                        break;
                        
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

            var repeatStatement = new RepeatUntilStatement
            {
                startToken = repeatToken,
                endToken = _stream.Current,
                statements = statements,
                condition = condition
            };
            if (error != null)
            {
                repeatStatement.Errors.Add(error);
            }

            return repeatStatement;
        }

        
        private WhileStatement ParseWhileStatement(Token whileToken)
        {
            var condition = ParseWikiExpression();
            var statements = new List<IStatementNode>();
            var looking = true;
            ParseError error = null;
            while (looking)
            {
                var nextToken = _stream.Peek;
                switch (nextToken.type)
                {
                    case LexemType.EOF:
                        error = new ParseError(whileToken, ErrorCodes.WhileStatementMissingEndWhile);
                        looking = false;
                        break;
                        
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

            var whileStatement = new WhileStatement
            {
                startToken = whileToken,
                endToken = _stream.Current,
                statements = statements,
                condition = condition
            };
            if (error != null)
            {
                whileStatement.Errors.Add(error);
            }

            return whileStatement;
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

            ParseError error = null;
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

                                error = new ParseError(ifToken, ErrorCodes.IfStatementMissingEndIf);
                                looking = false;
                                break;
                                
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

                    var ifStatement = new IfStatement(ifToken, _stream.Current, condition, positiveStatements, negativeStatements);
                    if (error != null)
                    {
                        ifStatement.Errors.Add(error);
                    }

                    return ifStatement;
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
                    var patchToken = _stream.CreatePatchToken(LexemType.VariableGeneral, "_")[0];
                    var statement = new GotoStatement(gotoToken, patchToken);
                    statement.Errors.Add(new ParseError(gotoToken, ErrorCodes.GotoMissingLabel));
                    return statement;
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
                    var patchToken = _stream.CreatePatchToken(LexemType.VariableGeneral, "_")[0];
                    var statement = new GoSubStatement(gotoToken, patchToken);
                    statement.Errors.Add(new ParseError(gotoToken, ErrorCodes.GoSubMissingLabel));
                    return statement;
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
                    var error = new ParseError(token, ErrorCodes.ExpectedParameter);
                    var patchToken = _stream.CreatePatchToken(LexemType.VariableGeneral, "_")[0];
                    var fake = new TypeDefinitionMember(patchToken, patchToken, new VariableRefNode(patchToken),
                        new TypeReferenceNode(VariableType.Integer, patchToken));
                    fake.Errors.Add(error);
                    return fake;
            }
        }

        private TypeDefinitionStatement ParseTypeDefinition(Token start)
        {
            // var nameToken = _stream.Advance();
            var errors = new List<ParseError>();

            var nameToken = _stream.Peek;
            if (nameToken.type != LexemType.VariableGeneral)
            {
                errors.Add(new ParseError(start, ErrorCodes.TypeDefMissingName));
                nameToken = _stream.CreatePatchToken(LexemType.VariableGeneral, "_")[0];
            }
            else
            {
                _stream.Advance(); // move past name
            }
            
            var members = new List<TypeDefinitionMember>();
            var name = new VariableRefNode(nameToken);

            var lookingForMembers = true;
            while (lookingForMembers)
            {
                var peek = _stream.Peek;
                switch (peek.type)
                {
                    case LexemType.EOF:
                        errors.Add(new ParseError(start, ErrorCodes.TypeDefMissingEndType));
                        lookingForMembers = false;
                        break;
                                
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

            var statement = new TypeDefinitionStatement(start, _stream.Current, name, members);
            statement.Errors = errors;
            return statement;
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

                    if (token.type == LexemType.EndStatement)
                    {
                        var patchToken = _stream.CreatePatchToken(LexemType.KeywordTypeInteger, "integer", -2)[0];
                        var node = new TypeReferenceNode(patchToken);
                        node.Errors.Add(new ParseError(patchToken, ErrorCodes.DeclarationMissingTypeRef));
                        return node;
                    }
                    else
                    {
                        var patchToken = _stream.CreatePatchToken(LexemType.KeywordTypeInteger, "integer", -1)[0];
                        var node = new TypeReferenceNode(patchToken);
                        node.Errors.Add(new ParseError(patchToken, ErrorCodes.DeclarationInvalidTypeRef));
                        return node;
                    }
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
            return TryParseExpression(out expr, out _);
        }
        
        public bool TryParseExpression(out IExpressionNode expr, out ProgramRecovery recovery)
        {
            expr = null;
            if (!TryParseWikiTerm(out var term, out recovery))
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
                    var patchToken = _stream.CreatePatchToken(LexemType.LiteralInt, "0", -2)[0];
                    var error = new ParseError(patchToken, ErrorCodes.ExpectedLiteralInt);
                    var node = new LiteralIntExpression(patchToken);
                    node.Errors.Add(error);
                    return node;
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


            if (!TryParseWikiTerm(out var expr, out var error))
            {
                _stream.Patch(error.index, error.correctiveTokens);
                if (!TryParseWikiTerm(out expr, out var nextError))
                {
                    throw new Exception("Unable to patch stream");
                }
                else
                {
                    expr.Errors.Add(error.error);
                }
            }
            
            return expr;
        }
        
        private bool TryParseWikiTerm(out IExpressionNode outputExpression, out ProgramRecovery recovery)
        {
            var token = _stream.Peek;
            recovery = null;
            switch (token.type)
            {
                
                case LexemType.CommandWord:
                    _stream.Advance();

                    // ParseCommandOverload(token, out var command, out var argExpressions);
                    ParseCommandOverload2(token, out var command, out var argExpressions, out var argMap, out var errors);
                    
                    // // parse the args!
                    // var argExpressions = ParseCommandArgs(token, command);
                    outputExpression = new CommandExpression()
                    {
                        startToken = token,
                        endToken = _stream.Current,
                        command = command,
                        args = argExpressions,
                        argMap = argMap
                    };
                    outputExpression.Errors.AddRange(errors);
                    
                    break;
                case LexemType.ParenOpen:


                    var checkpoint = _stream.Save();
                    _stream.Advance();

                    if (!TryParseExpression(out var expr, out var innerRecovery))
                    {
                        recovery = new ProgramRecovery(
                            new ParseError(_stream.Current, ErrorCodes.ExpressionMissingAfterOpenParen),
                            _stream.Index, innerRecovery, new List<Token>
                            {
                                new Token { lexem = new Lexem(LexemType.ParenClose) }
                            }
                        );
                        _stream.Restore(checkpoint);
                        outputExpression = null;
                       
                        return false;
                    }
                    var closeToken = _stream.Advance(); // move past closing...

                    if (closeToken.type != LexemType.ParenClose)
                    {
                        recovery = new ProgramRecovery(
                            new ParseError(token, ErrorCodes.ExpressionMissingCloseParen),
                            _stream.Index - 1, new List<Token>
                            {
                                new Token { lexem = new Lexem(LexemType.ParenClose) }
                            }
                        );
                        _stream.Restore(checkpoint);
                        outputExpression = null;
                        return false;
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
                    recovery = new ProgramRecovery(new ParseError(_stream.Current, ErrorCodes.ExpressionMissing),
                        _stream.Index, _stream.CreatePatchToken(LexemType.LiteralInt, "0"));
                    // recovery = new ProgramRecovery
                    // {
                    //     error = new ProgramError(_stream.Current, ErrorCodes.ExpressionMissing),
                    //     correctiveTokens = _stream.CreatePatchToken(LexemType.LiteralInt, "0")
                    // };
                    break;
            }

            return outputExpression != null;
        }
        
    }
}
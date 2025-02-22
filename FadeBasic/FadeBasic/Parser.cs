using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using FadeBasic.Ast;
using FadeBasic.Ast.Visitors;
using FadeBasic.Virtual;
using TypeInfo = FadeBasic.Ast.TypeInfo;

namespace FadeBasic
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

        // public HashSet<string> labels = new HashSet<string>();
        public Dictionary<string, Symbol> labelTable = new Dictionary<string, Symbol>();
        public Dictionary<string, string> labelDeclTable = new Dictionary<string, string>(); // label -> function name
        public Dictionary<string, SymbolTable> typeNameToTypeMembers = new Dictionary<string, SymbolTable>();
        public Dictionary<string, TypeDefinitionStatement> typeNameToDecl = new Dictionary<string, TypeDefinitionStatement>();
        public SymbolTable globalVariables = new SymbolTable();
        public Stack<SymbolTable> localVariables = new Stack<SymbolTable>();
        public Stack<string> currentFunctionName = new Stack<string>();
        public Dictionary<string, Symbol> functionSymbolTable = new Dictionary<string, Symbol>();
        public Dictionary<string, FunctionStatement> functionTable = new Dictionary<string, FunctionStatement>();
        public Dictionary<string, List<TypeInfo>> functionReturnTypeTable = new Dictionary<string, List<TypeInfo>>();
        
        List<DelayedTypeCheck> delayedTypeChecks = new List<DelayedTypeCheck>();

        private int allowExitCounter;
        
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

        public void DeclareFunction(FunctionStatement function)
        {
            if (functionSymbolTable.TryGetValue(function.name, out var existing))
            {
                function.Errors.Add(new ParseError(function.nameToken, ErrorCodes.FunctionAlreadyDeclared)); 
            }
            else
            {
                functionSymbolTable.Add(function.name, new Symbol
                {
                    text = function.name, 
                    typeInfo = TypeInfo.Void, // TODO: it would be cool if we could get the exitfunction and endfunction return values...
                    source = function
                });
                functionTable.Add(function.name, function);
            }
        }

        // private HashSet<FunctionStatement> _begunFunctions = new HashSet<FunctionStatement>();
        public bool BeginFunction(FunctionStatement function)
        {
            currentFunctionName.Push(function.name);
            // if (_begunFunctions.Contains(function))
            // {
            //     return false;
            // }
            // _begunFunctions.Add(function);
            var parameters = function.parameters;
            var table = new SymbolTable();
            localVariables.Push(table);

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

            return true;
        }

        public void EndFunction()
        {
            localVariables.Pop();
            currentFunctionName.Pop();
        }

        public void BeginLoop() => allowExitCounter++;
        public void EndLoop() => allowExitCounter--;
        public bool AllowExits => allowExitCounter > 0;

        public string GetCurrentFunctionName() => currentFunctionName.Count > 0 ? currentFunctionName.Peek() : null;

        public void AddLabel(string funcName, LabelDeclarationNode labelDecl)
        {
            if (labelTable.ContainsKey(labelDecl.label))
            {
                labelDecl.Errors.Add(new ParseError(labelDecl, ErrorCodes.SymbolAlreadyDeclared));
            }
            else
            {
                labelTable[labelDecl.label] = new Symbol
                {
                    text = labelDecl.label,
                    source = labelDecl,
                    typeInfo = TypeInfo.Void
                };
                labelDeclTable[labelDecl.label] = funcName;
            }
        }


        public void EnforceOperatorTypes(BinaryOperandExpression expr)
        {
            // need to set the parsedType for the expr, and validate that the two types are okay to add...

            var lht = expr.lhs.ParsedType;
            var rht = expr.rhs.ParsedType;

       
            if (lht.type == rht.type && lht.structName == rht.structName)
            {
                
                if (expr.operationType == OperationType.EqualTo)
                {
                    // the expression has a VOID type  
                    return;
                }

                
                // all is well!
                expr.ParsedType = lht;
                return;
            }
            
            // uh oh, stuff is different :( 
            if (lht.type == VariableType.String || rht.type == VariableType.String)
            {
                expr.Errors.Add(new ParseError(expr, ErrorCodes.InvalidCast));
                expr.ParsedType = lht;
            } else if (lht.type == VariableType.Struct || rht.type == VariableType.Struct)
            {
                expr.Errors.Add(new ParseError(expr, ErrorCodes.InvalidCast));
                expr.ParsedType = lht;
            } 
            
            // float math always wins
            else if (lht.type == VariableType.Float)
            {
                expr.ParsedType = lht;
            } else if (rht.type == VariableType.Float)
            {
                expr.ParsedType = rht;
            }
            else
            {
                // TODO: fill in other cast operations... 
                expr.ParsedType = lht;
            }
        }
        
        public void EnforceTypeAssignment(IAstNode node, TypeInfo rightSide, TypeInfo leftSide, bool softLeft, out TypeInfo foundType)
        {
            foundType = rightSide;

            if (leftSide.IsArray)
            {
                node.Errors.Add(new ParseError(node, ErrorCodes.InvalidCast, $"cannot assign to array"));
            }

            if (!rightSide.unset && rightSide.type == VariableType.Void)
            {
                foundType = leftSide;
                node.Errors.AddTypeError(node, rightSide, leftSide);
                return;
            }

            if (rightSide.type == VariableType.Struct)
            {
                if (softLeft && leftSide.type == VariableType.Integer)
                {
                    // eh this is fine.
                    foundType = rightSide;
                    return;
                }
                if (leftSide.type != VariableType.Struct || leftSide.structName != rightSide.structName)
                {
                    foundType = leftSide;
                    node.Errors.AddTypeError(node, rightSide, foundType);
                    return;
                }
            } else if (leftSide.type == VariableType.Struct)
            {
                if (rightSide.type != VariableType.Struct || leftSide.structName != rightSide.structName)
                {
                    node.Errors.AddTypeError(node, rightSide, leftSide);
                    return;
                }
            }
            
            
            switch (rightSide.type)
            {
                case VariableType.String:
                    if (leftSide.type != VariableType.String)
                    {
                        // error! 
                        foundType = leftSide;
                        node.Errors.AddTypeError(node, rightSide, foundType);
                    }
                    break;
                    // if (leftSide.type == VariableType.String)
                    // {
                    //     foundType = leftSide;
                    //     node.Errors.AddTypeError(node, rightSide, foundType);
                    // }
                    // break;
                case VariableType.Word:
                case VariableType.DWord:
                case VariableType.DoubleFloat:
                case VariableType.Byte:
                case VariableType.Float:
                case VariableType.Boolean:
                case VariableType.DoubleInteger:
                case VariableType.Integer:
                    if (leftSide.type == VariableType.String)
                    {
                        foundType = leftSide;
                        node.Errors.AddTypeError(node, rightSide, foundType);
                    }
                    break;
            }
        }

        private HashSet<FunctionReturnStatement> processingReturns = new HashSet<FunctionReturnStatement>();

        public HashSet<string> functionCheck = new HashSet<string>();
        
        public TypeInfo GetFunctionTypeInfo(FunctionStatement function, EnsureTypeContext ctx)
        {
            
            // at this moment, we need the TypeInfo for the given function.
            //  either it exists (because we've already called this function)
            //  or we need to generate it, which may cause a cascade of calls and
            //  generate the type info for whole method swaths.

            // first, identify all return statements
            var returnStatements = new List<FunctionReturnStatement>();
            function.Visit(child =>
            {
                if (child is FunctionReturnStatement exit)
                {
                    returnStatements.Add(exit);
                }
            });
            if (function.hasNoReturnExpression)
            {
                // this implies there is an implicit VOID return type
                SetFunctionType(function, TypeInfo.Void, function);
            }
            
            // then, each return statement needs to be checked.
            //  it is possible that any return statement could
            //  result in a recursive call to this same method.

            if (returnStatements.Count == 0)
            {
                functionReturnTypeTable[function.name] = new List<TypeInfo>
                {
                    TypeInfo.Void
                };
                return TypeInfo.Void;
            }
            foreach (var exit in returnStatements)
            {
              
            
                // make a ctx _per_ return expression, so each expression thread get its own
                //  ability to create its own loops
                var subCtx = ctx.WithFunction(function);
                if (exit.returnExpression == null)
                {
                    SetFunctionType(function, TypeInfo.Void, function, exit.startToken);
                }
                else
                {
                    exit.returnExpression.EnsureVariablesAreDefined(this, subCtx);

                    if (!subCtx.HasLoop && !exit.returnExpression.ParsedType.unset)
                    {
                        SetFunctionType(function, exit.returnExpression);
                    }
                }
                
            }

            if (functionReturnTypeTable.TryGetValue(function.name, out var types))
            {
                return types[0];
            }
            else
            {
                // :( 
                return TypeInfo.Unset;
            }
        }

        public void AddAssignment(AssignmentStatement assignment, EnsureTypeContext ctx,
            out DeclarationStatement implicitDecl)
        {
            implicitDecl = null;
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
                        EnforceTypeAssignment(variableRef, assignment.expression.ParsedType, existingSymbol.typeInfo, false, out _);
                        variableRef.DeclaredFromSymbol = existingSymbol;
                    }
                    else // no symbol exists, so this is a defacto local variable
                    {
                        
                        var defaultTypeInfo = new TypeInfo
                        {
                            type = variableRef.DefaultTypeByName,
                        };
                        var rightType = assignment.expression.ParsedType;

                        EnforceTypeAssignment(variableRef, rightType, defaultTypeInfo, true, out var foundType);
                        
                        
                        var locals = GetVariables(DeclarationScopeType.Local);
                        var symbol = new Symbol
                        {
                            text = variableRef.variableName,
                            typeInfo = foundType,
                            source = assignment
                        };
                        locals.Add(variableRef.variableName, symbol);

                        var isArray = symbol.typeInfo.IsArray;
                        var needsImplicitAssignment =
                            symbol.typeInfo.type == VariableType.Struct || isArray;
                        if (needsImplicitAssignment)
                        {
                            implicitDecl = DeclarationStatement.FromAssignment(variableRef, assignment, symbol);
                            if (isArray)
                            {
                                implicitDecl = null;
                                assignment.Errors.Add(new ParseError(assignment.variable, ErrorCodes.ImplicitArrayDeclaration));
                            }
                        }
                    }
                    break;
                case ArrayIndexReference indexRef: // a(1,2) = 1
                    // it isn't possible to assign an array without declaring it first- 
                    //  which means no new scopes need to be added.
                    if (TryGetSymbol(indexRef.variableName, out var existingArrSymbol))
                    {

                        var nonArrayVersion = new TypeInfo
                        {
                            structName = existingArrSymbol.typeInfo.structName,
                            type = existingArrSymbol.typeInfo.type,
                        };

                        foreach (var arg in indexRef.rankExpressions)
                        {
                            arg.EnsureVariablesAreDefined(this, ctx);
                            if (arg.ParsedType.type != VariableType.Integer)
                            {
                                arg.Errors.Add(new ParseError(arg, ErrorCodes.ArrayRankMustBeInteger));
                            }
                        }
                        
                        EnforceTypeAssignment(indexRef, assignment.expression.ParsedType, nonArrayVersion, false, out _);
                        indexRef.DeclaredFromSymbol = existingArrSymbol;
                    }
                    // EnforceTypeAssignment(variableRef, rightType, defaultTypeInfo, true, out var foundType);


                    break;
                case DeReference deRef: // *x = 3
                    // it isn't possible to de-ref a vairable without declaring it first- 
                    //  which means no new scopes need to be added.
                    break;
                case StructFieldReference structRef: // a.x.y = 1
                    // it isn't possible to assign to a struct field without declaring it first- 
                    //  which means no new scopes need to be added.
                    // but on the other hand, we do need to validate that the variable exists!
                    break;
            }
            
        }

        // public void GetParsedType(ITypeReferenceNode typeNode, string variableName)
        // {
        //     
        //     switch (typeNode)
        //     {
        //         case TypeReferenceNode typeReference:
        //             var symbol = new Symbol
        //             {
        //                 text = declStatement.variable,
        //                 typeInfo = TypeInfo.FromVariableType(typeReference.variableType,
        //                     rank: declStatement.ranks?.Length ?? 0,
        //                     structName: null),
        //                 source = declStatement
        //             };
        //             table.Add(declStatement.variable, symbol);
        //             declStatement.ParsedType = typeReference.ParsedType = symbol.typeInfo;
        //
        //             break;
        //         case StructTypeReferenceNode structTypeReference:
        //             if (!TryGetType(structTypeReference.variableNode.variableName, out _))
        //             {
        //                 structTypeReference.Errors.Add(new ParseError(structTypeReference, ErrorCodes.UnknownType));
        //             }
        //             symbol = new Symbol
        //             {
        //                 text = declStatement.variable,
        //                 typeInfo = TypeInfo.FromVariableType(structTypeReference.variableType,
        //                     rank: declStatement.ranks?.Length ?? 0,
        //                     structName: structTypeReference.variableNode.variableName),
        //                 source = declStatement
        //             };
        //             table.Add(declStatement.variable, symbol);
        //             declStatement.ParsedType = structTypeReference.ParsedType = symbol.typeInfo;
        //             break;
        //     }
        // }
        
        public void AddDeclaration(DeclarationStatement declStatement, EnsureTypeContext ctx)
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

                    if (declStatement.ranks != null)
                    {
                        for (var i = 0; i < declStatement.ranks.Length; i++)
                        {
                            var rankExpr = declStatement.ranks[i];
                            if (rankExpr == null) continue;
                            rankExpr.EnsureVariablesAreDefined(this, ctx);
                            if (rankExpr.ParsedType.type != VariableType.Integer)
                            {
                                rankExpr.Errors.Add(new ParseError(rankExpr, ErrorCodes.ArrayRankMustBeInteger));
                            }
                        }
                    }
                    
                    var symbol = new Symbol
                    {
                        text = declStatement.variable,
                        typeInfo = TypeInfo.FromVariableType(typeReference.variableType,
                            rankExpressions: declStatement.ranks,
                            structName: null),
                        source = declStatement
                    };
                    table.Add(declStatement.variable, symbol);
                    declStatement.ParsedType = typeReference.ParsedType = symbol.typeInfo;

                    break;
                case StructTypeReferenceNode structTypeReference:
                    if (!TryGetType(structTypeReference.variableNode.variableName, out _))
                    {
                        structTypeReference.Errors.Add(new ParseError(structTypeReference, ErrorCodes.UnknownType));
                    }
                    symbol = new Symbol
                    {
                        text = declStatement.variable,
                        typeInfo = TypeInfo.FromVariableType(structTypeReference.variableType,
                            rankExpressions: declStatement.ranks,
                            structName: structTypeReference.variableNode.variableName),
                        source = declStatement
                    };
                    table.Add(declStatement.variable, symbol);
                    declStatement.ParsedType = structTypeReference.ParsedType = symbol.typeInfo;
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
            typeNameToDecl[type.name.variableName] = type;
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

        public bool TryGetLabel(string label, out Symbol symbol)
        {
            return labelTable.TryGetValue(label, out symbol);
        }

        public void AddCommand(CommandExpression commandExpr, EnsureTypeContext ctx) =>
            AddCommand(commandExpr.command, commandExpr.args, commandExpr.argMap, ctx);

        public void ValidateCommandArgs(CommandInfo command, List<IExpressionNode> args, List<int> argMap,
            EnsureTypeContext ctx)
        {
            for (var argIndex = 0; argIndex < args.Count; argIndex++)
            {
                var arg = args[argIndex];
                var descriptor = command.args[argMap[argIndex]];
                            
                            
                arg.EnsureVariablesAreDefined(this, ctx);

                if (TypeInfo.TryGetFromTypeCode(descriptor.typeCode, out var guessType))
                {
                    this.EnforceTypeAssignment(arg, arg.ParsedType, guessType, false, out _);
                } else if (descriptor.typeCode == TypeCodes.ANY)
                {
                    // this is sort of hack, to pass the lhs AS the same type as the arg...
                    //  but since its ANYTHING, then it may as well be the same?
                    var impliedLhs = arg.ParsedType;
                    var prevCount = arg.Errors.Count;
                    this.EnforceTypeAssignment(arg, arg.ParsedType, impliedLhs, false,  out _);
                    if (prevCount != arg.Errors.Count)
                    {
                        // sneaky way to know that a new error has been added, and we are going to hack the display...
                        var err = arg.Errors[arg.Errors.Count - 1];
                        var replace = ParseErrorExtensions.ConvertTypeInfoToName(arg.ParsedType);
                        err.message = err.message.Substring(0, err.message.Length - replace.Length ) + "any";
                    }
                }
                
            }
        }
        
        public void AddCommand(CommandInfo command, List<IExpressionNode> args, List<int> argMap, EnsureTypeContext ctx)
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
                        AddCommand(commandExpr.command, commandExpr.args, commandExpr.argMap, ctx);
                        break;
                }
            }
            
            ValidateCommandArgs(command, args, argMap, ctx);

        }

        public void SetFunctionType(FunctionStatement function, IExpressionNode returnExpr)
        {
            var returnExpressionParsedType = returnExpr.ParsedType;
            SetFunctionType(function, returnExpressionParsedType, returnExpr);
        }
        public void SetFunctionType(FunctionStatement function, TypeInfo type, IAstNode srcNode, Token token=null)
        {
           
            
            if (!functionReturnTypeTable.TryGetValue(function.name, out var types))
            {
                functionReturnTypeTable[function.name] = new List<TypeInfo>
                {
                    type
                };
                if (type.IsArray)
                {
                    srcNode.Errors.Add(new ParseError(srcNode, ErrorCodes.InvalidFunctionReturnType));
                }
                return;
            }
            
            for (var i = 0; i < types.Count; i++)
            {
                var existingType = types[i];
                if (existingType.type == type.type &&
                    existingType.structName == type.structName)
                {
                    // found the type! nothing to do :) 
                    return;
                }
            }
            
            // we didn't find the type; that means its another error :( 
            types.Add(type);
            var error = token == null
                ? new ParseError(srcNode, ErrorCodes.AmbiguousFunctionReturnType)
                : new ParseError(token, ErrorCodes.AmbiguousFunctionReturnType);
            srcNode.Errors.Add(error);
        }

        public void DoDelayedTypeChecks()
        {
            foreach (var check in delayedTypeChecks)
            {
                EnforceTypeAssignment(check.source, check.right.ParsedType, check.left.ParsedType, false, out _);
            }
        }
        
        public void AddDelayedTypeCheck(IAstNode source, IAstNode right, IAstNode left)
        {
            delayedTypeChecks.Add(new DelayedTypeCheck
            {
                source = source,
                left = left,
                right = right
            });
        }

        class DelayedTypeCheck
        {
            public IAstNode source, right, left;
        }
    }

    public class ParseOptions
    {
        public bool ignoreChecks = false;

        public static readonly ParseOptions Default = new ParseOptions
        {
            ignoreChecks = false
        };
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

        public ProgramNode ParseProgram(ParseOptions options = null)
        {
            if (options == null) options = ParseOptions.Default;
            
            var program = new ProgramNode(_stream.Current);

            while (!_stream.IsEof)
            {
                var statement = ParseStatement(program.statements);
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
            program.AddScopeRelatedErrors(options);
            
            return program;
        }
        

        private IStatementNode ParseStatementThatStartsWithScope(Token scopeToken)
        {
            // we know this must be a declaration node.

            var next = _stream.Advance();
            switch (next.type)
            {
                case LexemType.VariableReal:
                case LexemType.VariableString:
                case LexemType.VariableGeneral:
                    var variableRefNode = new VariableRefNode(next);
                    // we know we have something that looks like "local x", so the next token MUST be "as", and then there MUST be a type.
                    var asToken = _stream.Peek;
                    ParseError error = null;
                    ITypeReferenceNode typeReference;
                    IExpressionNode initializer = null;

                    switch (asToken.type)
                    {
                        case LexemType.KeywordAs:
                            _stream.Advance(); // skip 'as'
                            typeReference = ParseTypeReference();

                            break;
                        case LexemType.OpEqual:
                            // type reference is implied from variable, now.
                            typeReference = new TypeReferenceNode(variableRefNode.DefaultTypeByName, next);
                            _stream.Advance(); // discard equal sign
                            initializer = ParseWikiExpression();
                            break;
                        case LexemType.EndStatement:
                            // eh, this is fine...
                            typeReference = new TypeReferenceNode(variableRefNode.DefaultTypeByName, next);
                            break;
                        default:
                            typeReference = new TypeReferenceNode(VariableType.Integer, next);
                            error = new ParseError(asToken, ErrorCodes.ScopedDeclarationExpectedAs);
                            break;
                    }
                    
                    // if (asToken.type != LexemType.KeywordAs)
                    // {
                    //     error = new ParseError(asToken, ErrorCodes.ScopedDeclarationExpectedAs);
                    // }
                    // else
                    // {
                    //     _stream.Advance(); // skip As token
                    // }
                    
                    // var typeReference = ParseTypeReference();
                    if (error != null)
                    {
                        typeReference.Errors.Insert(0, error);
                    }
                    var declStatement = new DeclarationStatement(scopeToken, new VariableRefNode(next), typeReference);
                    if (_stream.Peek.lexem.type == LexemType.OpEqual)
                    {
                        if (initializer != null)
                        {
                            throw new Exception("cannot have multiple initializers");
                        }
                        // an initializer expression has been found!
                        _stream.Advance(); // discard equal sign
                        initializer = ParseWikiExpression();
                    }

                    declStatement.initializerExpression = initializer;
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
                                    case LexemType.EndStatement:
                                    case LexemType.EOF:

                                        error = new ParseError(next,
                                            ErrorCodes.VariableIndexMissingCloseParen);
                                        // _stream.Patch();
                                        // _stream.Advance()
                                        
                                        // normally we would pretend this is a ) to signal the end,
                                        //  but since this was triggered by EOF, there is no point looking forward anyway.
                                        looking = false; 
                                        break;
                                        
                                    // case LexemType.EndStatement:
                                    case LexemType.ArgSplitter:
                                        _stream.Advance();
                                        break;
                                    case LexemType.ParenClose:
                                        _stream.Advance();
                                        looking = false;
                                        break;
                                    default:
                                        if (TryParseExpression(out var expr))
                                        {
                                            rankExpressions.Add(expr);
                                        }
                                        else
                                        {
                                            // an error happened, because we did not find a close paren
                                            looking = false;
                                            error = new ParseError(next,
                                                ErrorCodes.VariableIndexMissingCloseParen);
                                        }
                                        
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

        private IStatementNode ParseStatement(List<IStatementNode> statementGroup, bool consumeEndOfStatement=true)
        {
            IStatementNode Inner()
            {
                IStatementNode lastStatement = null;
                if (statementGroup != null && statementGroup.Count > 0)
                {
                    lastStatement = statementGroup[statementGroup.Count - 1];
                }
                
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

                                var maybeEqual = _stream.Peek;
                                if (maybeEqual.lexem.type == LexemType.OpEqual)
                                {
                                    // ah, there is an assignment happening here too!
                                    _stream.Advance(); // discard the equal sign.
                                    decl.initializerExpression = ParseWikiExpression();
                                    
                                }
                                
                                return decl;
                            
                            
                            // -= += ...
                            case LexemType.OpMinus:
                            case LexemType.OpPlus:
                            case LexemType.OpMultiply:
                            case LexemType.OpDivide:
                                var thirdToken = _stream.Advance();
                                switch (thirdToken.type)
                                {
                                    case LexemType.OpEqual:
                                        var rhsExpr = ParseWikiExpression();

                                        var opExpr = new BinaryOperandExpression(token, rhsExpr.EndToken, secondToken,
                                            reference, rhsExpr);
                                        return new AssignmentStatement
                                        {
                                            startToken = token,
                                            endToken = rhsExpr.EndToken,
                                            variable = reference,
                                            expression = opExpr
                                        };
                                        break;
                                    default:
                                        _stream.AdvanceUntil(LexemType.EndStatement);
                                        var invalidMathShortcutStatement = new NoOpStatement();
                                        invalidMathShortcutStatement.Errors.Add(new ParseError(thirdToken, ErrorCodes.AmbiguousDeclarationOrAssignment));
                                        return invalidMathShortcutStatement;

                                }
                                break;
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
                            endToken = GetLastToken(token, commandArgs),
                            command = command,
                            args = commandArgs,
                            argMap = argMap,
                        };
                       
                        commandStatement.Errors.AddRange(errors);
                        return commandStatement;

                    case LexemType.KeywordType:
                        return ParseTypeDefinition(token);
                        
                        break;
                    case LexemType.ArgSplitter when lastStatement is AssignmentStatement lastAssign:
                        var hopefullyAnAssignment = ParseStatement(statementGroup);
                        if (!(hopefullyAnAssignment is AssignmentStatement))
                        {                                    
                            hopefullyAnAssignment.Errors.Add(new ParseError(hopefullyAnAssignment, ErrorCodes.MultiLineAssignmentCannotInferType));
                        }
                        return hopefullyAnAssignment;
                    case LexemType.ArgSplitter when lastStatement is DeclarationStatement lastDeclare:
                        hopefullyAnAssignment = ParseStatement(statementGroup);
                        if (hopefullyAnAssignment is AssignmentStatement declAssign)
                        {
                            // convert this into a declaration!

                            if (declAssign.variable is VariableRefNode declRef)
                            {
                                if (declRef.DefaultTypeByName != lastDeclare.type.variableType)
                                {
                                    hopefullyAnAssignment.Errors.Add(new ParseError(hopefullyAnAssignment, ErrorCodes.MultiLineDeclareCannotInferType));
                                    return hopefullyAnAssignment;
                                    
                                }
                                var fakeDecl = new DeclarationStatement(
                                    lastDeclare.scopeType == DeclarationScopeType.Global ? Token.Global : Token.Local, declRef, lastDeclare.type
                                );
                                fakeDecl.initializerExpression = declAssign.expression;
                                return fakeDecl;
                            }
                            else
                            {
                                hopefullyAnAssignment.Errors.Add(new ParseError(hopefullyAnAssignment, ErrorCodes.MultiLineDeclareInvalidVariable));
                                return hopefullyAnAssignment;
                            }
                            

                        } else if (!(hopefullyAnAssignment is DeclarationStatement))
                        {
                            
                            hopefullyAnAssignment.Errors.Add(new ParseError(hopefullyAnAssignment, ErrorCodes.MultiLineDeclareCannotInferType));

                        }
                        return hopefullyAnAssignment;
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
                            if (TryParseExpression(out var arg))
                            {
                                commandArgs.Add(arg);
                                argMap.Add(0);
                                //arg = new LiteralIntExpression(_stream.CreatePatchToken(LexemType.LiteralInt, "0")[0]);
                            }
                            else
                            {
                                isDone = true;
                                break;
                            }
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
            IExpressionNode returnExpression = null;
            if (TryParseExpression(out returnExpression))
            {
                
            }
            // var expression = ParseWikiExpression();
            return new FunctionReturnStatement(endToken, returnExpression);
        }
        
        private FunctionStatement ParseFunction(Token functionToken)
        {
            var errors = new List<ParseError>();

            // parse the name
            var nameToken = _stream.Peek;
            if (nameToken.type == LexemType.CommandWord)
            {
                nameToken = _stream.CreatePatchToken(LexemType.VariableGeneral, "_")[0];
                errors.Add(new ParseError(functionToken, ErrorCodes.FunctionCannotUseCommandName));
            }
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
            var labels = new List<LabelDeclarationNode>();
            looking = true;
            bool hasNoReturnExpression = false;
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
                        else
                        {
                            hasNoReturnExpression = true;
                        }
                        
                        looking = false;
                        break;
                    case LexemType.KeywordFunction:
                        var illegalFunctionMember = ParseStatement(statements);
                        statements.Add(illegalFunctionMember);
                        illegalFunctionMember.Errors.Add(new ParseError(nextToken, ErrorCodes.FunctionDefinedInsideFunction));
                        break;
                    default:
                        var member = ParseStatement(statements);
                        if (member is LabelDeclarationNode lbl)
                        {
                            labels.Add(lbl);
                        }
                        else
                        {
                            statements.Add(member);
                        }
                        break; 
                }
            }


            return new FunctionStatement
            {
                nameToken = nameToken,
                Errors = errors,
                statements = statements,
                parameters = parameters,
                labels = labels,
                name = nameToken.caseInsensitiveRaw,
                startToken = functionToken,
                endToken = _stream.Current,
                hasNoReturnExpression = hasNoReturnExpression
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
                        var member = ParseStatement(statements);
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
                        var member = ParseStatement(statements);
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
                        var member = ParseStatement(statements);
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
                        var member = ParseStatement(statements);
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
                        var member = ParseStatement(statements);
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
                        var member = ParseStatement(statements);
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
                                var member = ParseStatement(statements, consumeEndOfStatement: false);
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
                                var member = ParseStatement(statements);
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
                    case LexemType.ArgSplitter:
                        _stream.Advance();
                        var member = ParseTypeMember();
                        members.Add(member);
                        break;
                    default:
                        member = ParseTypeMember();
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
                case LexemType.KeywordXor:
                    return 8;
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
                
                case LexemType.OpBitwiseLeftShift:
                case LexemType.OpBitwiseRightShift:
                    return 50;
                case LexemType.OpBitwiseXor:
                    return 53;
                case LexemType.OpBitwiseOr:
                    return 54;
                case LexemType.OpBitwiseAnd:
                    return 55;
                case LexemType.OpBitwiseNot:
                    return 56;
           
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
                case LexemType.OpBitwiseLeftShift:
                case LexemType.OpBitwiseRightShift:
                case LexemType.OpBitwiseNot:
                    return false;
                case LexemType.OpPlus:
                case LexemType.OpMultiply:
                case LexemType.OpEqual:
                case LexemType.OpNotEqual:
                case LexemType.KeywordAnd:
                case LexemType.KeywordXor:
                case LexemType.KeywordOr:
                case LexemType.OpBitwiseOr:
                case LexemType.OpBitwiseAnd:
                case LexemType.OpBitwiseXor:
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
                case LexemType.KeywordXor:
                case LexemType.KeywordOr:
                case LexemType.OpBitwiseAnd:
                case LexemType.OpBitwiseOr:
                case LexemType.OpBitwiseNot:
                case LexemType.OpBitwiseXor:
                case LexemType.OpBitwiseLeftShift:
                case LexemType.OpBitwiseRightShift:
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

                lhs = new BinaryOperandExpression(start: lhs.StartToken, end: rhs.EndToken, op: op, lhs: lhs, rhs: rhs);
            }
            
            return lhs;
        }

        private ILiteralNode ParseLiteral(Token token)
        {
            switch (token.type)
            {
                case LexemType.LiteralBinary:
                case LexemType.LiteralHex:
                case LexemType.LiteralOctal:
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
                    case LexemType.LiteralBinary:
                    case LexemType.LiteralHex:
                    case LexemType.LiteralOctal:
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
                        endToken = GetLastToken(token, argExpressions),
                        command = command,
                        args = argExpressions,
                        argMap = argMap
                    };
                    // outputExpression.EndToken
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
                case LexemType.LiteralBinary:
                case LexemType.LiteralOctal:
                case LexemType.LiteralHex:
                case LexemType.LiteralReal:
                case LexemType.LiteralString:
                    _stream.Advance();

                    outputExpression = ParseLiteral(token);
                    break;
                case LexemType.OpMultiply:
                    _stream.Advance();

                    var deRefExpr = ParseVariableReference();
                    outputExpression = new DereferenceExpression(deRefExpr, token);
                    outputExpression.Errors.Add(new ParseError(token, ErrorCodes.PointersAreNotSupported));
                    break;
                case LexemType.OpMinus:
                    _stream.Advance();

                    var negateExpr = ParseWikiTerm();
                    outputExpression = new UnaryOperationExpression(UnaryOperationType.Negate, negateExpr, token, _stream.Current);
                    break;
                case LexemType.OpBitwiseNot:
                    _stream.Advance();

                    var bpeek = _stream.Peek;
                    var bnotExpr = ParseWikiExpression();
                    
                    outputExpression = new UnaryOperationExpression(UnaryOperationType.BitwiseNot, bnotExpr, token, _stream.Current);
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

        static Token GetLastToken<T>(Token defaultToken, List<T> subList)
            where T : IAstNode
        {
            if (subList?.Count > 0) return subList[subList.Count - 1].EndToken;
            return defaultToken;
        }
    }
    
}
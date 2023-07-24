using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection.Emit;
using DarkBasicYo.Ast;

namespace DarkBasicYo.Virtual
{
    public class CompiledVariable
    {
        public byte byteSize;
        public byte typeCode;
        public string name;
        public byte registerAddress;
    }

    public class CompiledArrayVariable
    {
        public byte byteSize;
        public byte typeCode;
        public string name;
        public byte registerAddress;
        public byte[] rankSizeRegisterAddresses; // an array where the index is the rank, and the value is the ptr to a register whose value holds the size of the rank
        public byte[] rankIndexScalerRegisterAddresses; // an array where the index is the rank, and the value is the ptr to a register whose value holds the multiplier factor for the rank's indexing
    }
    
    public class Compiler
    {
        public readonly CommandCollection commands;
        private List<byte> _buffer = new List<byte>();

        public List<byte> Program => _buffer;
        public int registerCount;

        private Dictionary<string, CompiledVariable> _varToReg = new Dictionary<string, CompiledVariable>();

        private Dictionary<string, CompiledArrayVariable> _arrayVarToReg =
            new Dictionary<string, CompiledArrayVariable>();
        
        public HostMethodTable methodTable;

        private Dictionary<CommandDescriptor, int> _commandToPtr = new Dictionary<CommandDescriptor, int>();

        public Compiler(CommandCollection commands)
        {
            this.commands = commands;

            var methods = new HostMethod[commands.Commands.Count];
            for (var i = 0; i < commands.Commands.Count; i++)
            {
                methods[i] = HostMethodUtil.BuildHostMethodViaReflection(commands.Commands[i].method);
                _commandToPtr[commands.Commands[i]] = i;
            }
            methodTable = new HostMethodTable
            {
                methods = methods
            };
            
        }

        public void Compile(ProgramNode program)
        {
            foreach (var statement in program.statements)
            {
                Compile(statement);
            }
        }

        public void Compile(IStatementNode statement)
        {
            switch (statement)
            {
                case DeclarationStatement declarationStatement:
                    Compile(declarationStatement);
                    break;
                case AssignmentStatement assignmentStatement:
                    Compile(assignmentStatement);
                    break;
                case CommandStatement commandStatement:
                    Compile(commandStatement);
                    break;
                default:
                    throw new Exception("compiler exception: unhandled statement node");
            }
        }

        public void Compile(CommandStatement commandStatement)
        { 
            // TODO: save local state?
            
            // put each expression on the stack.
            foreach (var argExpr in commandStatement.args)
            {
                Compile(argExpr);
            }
            
            // find the address of the method
            if (!_commandToPtr.TryGetValue(commandStatement.command, out var commandAddress))
            {
                throw new Exception("compiler: could not find method address: " + commandStatement.command);
            }
            
            _buffer.Add(OpCodes.PUSH);
            _buffer.Add(TypeCodes.INT);
            var bytes = BitConverter.GetBytes(commandAddress);
            for (var i = bytes.Length -1; i >= 0; i--)
            {
                _buffer.Add(bytes[i]);
            }

            _buffer.Add(OpCodes.CALL_HOST);
        }
        
        public void Compile(DeclarationStatement declaration)
        {
            /*
             * the declaration tells us that we need a register
             */
            // then, we need to reserve a register for the variable.
            var tc = VmUtil.GetTypeCode(declaration.type.variableType);

            if (declaration.ranks == null || declaration.ranks.Length == 0)
            {
                // this is a normal variable decl.
                _varToReg[declaration.variable] = new CompiledVariable
                {
                    registerAddress = (byte)(registerCount++),
                    name = declaration.variable,
                    typeCode = tc,
                    byteSize = TypeCodes.GetByteSize(tc)
                };
            }
            else
            {
                // this is an array decl
                var arrayVar = _arrayVarToReg[declaration.variable] = new CompiledArrayVariable()
                {
                    registerAddress = (byte)(registerCount++),
                    rankSizeRegisterAddresses = new byte[declaration.ranks.Length],
                    rankIndexScalerRegisterAddresses = new byte[declaration.ranks.Length],
                    name = declaration.variable,
                    typeCode = tc,
                    byteSize = TypeCodes.GetByteSize(tc)
                };
                
                // this is an array! we need to save each rank's length
                for (var i = declaration.ranks.Length -1; i >= 0; i--)
                {
                    // put the expression value onto the stack
                    var expr = declaration.ranks[i];
                    Compile(expr); 
                    
                    // reserve 2 registers for array rank metadata
                    arrayVar.rankSizeRegisterAddresses[i] = (byte)(registerCount++);
                    arrayVar.rankIndexScalerRegisterAddresses[i] = (byte)(registerCount++);
                    
                    // store the expression value (the length for this rank) in a register
                    _buffer.Add(OpCodes.STORE);
                    _buffer.Add(arrayVar.rankSizeRegisterAddresses[i]); 

                    if (i == declaration.ranks.Length - 1)
                    {
                        // push 1 as the multiplier factor, because later, multiplying by 1 is a no-op;
                        _buffer.Add(OpCodes.PUSH);
                        _buffer.Add(TypeCodes.INT);
                        _buffer.Add(0);
                        _buffer.Add(0);
                        _buffer.Add(0);
                        _buffer.Add(1);
                    }
                    else
                    {
                        // get the length of the right term
                        _buffer.Add(OpCodes.LOAD);
                        _buffer.Add(arrayVar.rankSizeRegisterAddresses[i + 1]); 
                        
                        // and get the multiplier factor of the right term
                        _buffer.Add(OpCodes.LOAD);
                        _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i + 1]); 

                        // and multiply those together...
                        _buffer.Add(OpCodes.MUL);
                    }
                    
                    _buffer.Add(OpCodes.STORE);
                    _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i]); // store the multiplier 
                }
                
                
                
                // now, we need to allocate enough memory for the entire thing
                _buffer.Add(OpCodes.PUSH); // push the value '1' onto the stack...
                _buffer.Add(TypeCodes.INT);
                _buffer.Add(0);
                _buffer.Add(0);
                _buffer.Add(0);
                _buffer.Add(1);
                
                for (var i = 0; i < declaration.ranks.Length; i++)
                {
                    _buffer.Add(OpCodes.LOAD);
                    _buffer.Add(arrayVar.rankSizeRegisterAddresses[i]); // store the length of the sub var on the register.
                    _buffer.Add(OpCodes.MUL);
                }
                
                var sizeOfElement = TypeCodes.GetByteSize(tc);
                _buffer.Add(OpCodes.PUSH); // push the length
                _buffer.Add(TypeCodes.INT);
                _buffer.Add(0);
                _buffer.Add(0);
                _buffer.Add(0);
                _buffer.Add(sizeOfElement);
                
                _buffer.Add(OpCodes.MUL); // multiply the length by the size, to get the entire byte-size of the requested array
                _buffer.Add(OpCodes.ALLOC); // push the alloc instruction
                
                _buffer.Add(OpCodes.STORE);
                _buffer.Add(arrayVar.registerAddress);
            }
            
            
            // later in this compiler, when we find the variable assignment, we'll know where to find it.

            // but we do not actually need to emit any code at this point.
        }

        public void Compile(AssignmentStatement assignmentStatement)
        {
            /*
             * in order to assign, we need to know what we are assigning two, and find the correct place to put the result.
             *
             * If it is a simple variable, then it lives on a local register.
             * If it is an array, then it lives in memory.
             */

            
            // compile the rhs of the assignment...
            Compile(assignmentStatement.expression);

            switch (assignmentStatement.variable)
            {
                case ArrayIndexReference arrayRefNode:
                    if (!_arrayVarToReg.TryGetValue(arrayRefNode.variableName, out var compiledArrayVar))
                    {
                        throw new Exception("Compiler: cannot access array since it not declared" +
                                            arrayRefNode.variableName);
                    }
                    var sizeOfElement = compiledArrayVar.byteSize;

                    // always cast the expression to the correct type code; slightly wasteful, could be better.
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(compiledArrayVar.typeCode);
                    _buffer.Add(OpCodes.DISCARD); // we don't actually want the type code to live on the heap
                    
                    // load the size up
                    _buffer.Add(OpCodes.PUSH); // push the length
                    _buffer.Add(TypeCodes.INT);
                    _buffer.Add(0);
                    _buffer.Add(0);
                    _buffer.Add(0);
                    _buffer.Add(sizeOfElement);
                    
                    for (var i = 0; i < arrayRefNode.rankExpressions.Count ; i++)
                    {
                        _buffer.Add(OpCodes.LOAD); // load the multiplier factor for the term
                        _buffer.Add(compiledArrayVar.rankIndexScalerRegisterAddresses[i]);

                        var expr = arrayRefNode.rankExpressions[i];
                        Compile(expr); // load the expression index
                        
                        _buffer.Add(OpCodes.MUL);

                        if (i > 0)
                        {
                            _buffer.Add(OpCodes.ADD);
                        }
                    }
      
                    // get the size of the element onto the stack
                    _buffer.Add(OpCodes.PUSH); // push the length
                    _buffer.Add(TypeCodes.INT);
                    _buffer.Add(0);
                    _buffer.Add(0);
                    _buffer.Add(0);
                    _buffer.Add(sizeOfElement);
                    
                    // multiply the size of the element, and the index, to get the offset into the memory
                    _buffer.Add(OpCodes.MUL);
                    
                    // load the array's ptr onto the stack, this is for the math of the offset
                    _buffer.Add(OpCodes.LOAD); 
                    _buffer.Add(compiledArrayVar.registerAddress);

                    // add the offset to the original pointer to get the write location
                    _buffer.Add(OpCodes.ADD);
                    
                    // write! It'll find the ptr, then the size, and then the data itself
                    _buffer.Add(OpCodes.WRITE);
                    
                    break;
                case VariableRefNode variableRefNode:
                    
                    if (!_varToReg.TryGetValue(variableRefNode.variableName, out var compiledVar))
                    {
                        // // ?
                        var tc = VmUtil.GetTypeCode(variableRefNode.DefaultTypeByName);
                        compiledVar = _varToReg[variableRefNode.variableName] = new CompiledVariable
                        {
                            registerAddress = (byte)(registerCount++),
                            name = variableRefNode.variableName,
                            typeCode = tc,
                            byteSize = TypeCodes.GetByteSize(tc)
                        };
                    }
            
                    // always cast the expression to the correct type code; slightly wasteful, could be better.
                    _buffer.Add(OpCodes.CAST);
                    _buffer.Add(compiledVar.typeCode);
    
                    // store the value of the expression&cast in the desired register.
                    _buffer.Add(OpCodes.STORE);
                    _buffer.Add(compiledVar.registerAddress);
                    break;
                default:
                    throw new NotImplementedException("Unsupported reference assignment");
            }
         
            
            
        }
        
        
        public void Compile(IExpressionNode expr)
        {
            // CompiledVariable compiledVar = null;
            switch (expr)
            {
                case CommandExpression commandExpr:
                    foreach (var argExpr in commandExpr.args)
                    {
                        Compile(argExpr);
                    }
            
                    // find the address of the method
                    if (!_commandToPtr.TryGetValue(commandExpr.command, out var commandAddress))
                    {
                        throw new Exception("compiler: could not find method address: " + commandExpr.command);
                    }
            
                    _buffer.Add(OpCodes.PUSH);
                    _buffer.Add(TypeCodes.INT);
                    var bytes = BitConverter.GetBytes(commandAddress);
                    for (var i = bytes.Length -1; i >= 0; i--)
                    {
                        _buffer.Add(bytes[i]);
                    }

                    _buffer.Add(OpCodes.CALL_HOST);
                    break;
                case LiteralStringExpression literalString:
                    
                    // allocate some memory for a string...
                    var str = literalString.value;
                    var strSize = str.Length * TypeCodes.GetByteSize(TypeCodes.INT);
                    
                    // push the string data...
                    for (var i = str.Length - 1; i >= 0; i--)
                    {
                        var c = (uint)str[i];
                        AddPushUInt(_buffer, c, includeTypeCode:false);
                    }
                    
                    AddPushInt(_buffer, strSize); // SIZE, <Data>
                    
                    // this one will get used by the Write call
                    _buffer.Add(OpCodes.DUPE); // SIZE, SIZE, <Data>

                    // allocate a ptr to the stack
                    _buffer.Add(OpCodes.ALLOC); // PTR, SIZE, <Data>
                    
                    _buffer.Add(OpCodes.WRITE_PTR); // consume the ptr, then the length, then the data
                    
                    break;
                case LiteralRealExpression literalReal:
                    _buffer.Add(OpCodes.PUSH);
                    _buffer.Add(TypeCodes.REAL);
                    var realValue = BitConverter.GetBytes(literalReal.value);
                    for (var i = realValue.Length - 1; i >= 0; i--)
                    {
                        _buffer.Add(realValue[i]);
                    }
                    break;
                case LiteralIntExpression literalInt:
                    // push the literal value
                    AddPushInt(_buffer, literalInt.value);
                    // _buffer.Add(TypeCodes.INT);
                    // var value = BitConverter.GetBytes(literalInt.value);
                    // for (var i = value.Length - 1; i >= 0; i--)
                    // {
                    //     _buffer.Add(value[i]);
                    // }
                    break;
                case ArrayIndexReference arrayRef:
                    // need to fetch the value from the array...

                    if (!_arrayVarToReg.TryGetValue(arrayRef.variableName, out var arrayVar))
                    {
                        throw new Exception("compiler exception! the referenced array has not been declared yet " +
                                            arrayRef.variableName);
                    }
                    
                    var sizeOfElement = arrayVar.byteSize;

                    
                    // load the size up
                    _buffer.Add(OpCodes.PUSH); // push the length
                    _buffer.Add(TypeCodes.INT);
                    _buffer.Add(0);
                    _buffer.Add(0);
                    _buffer.Add(0);
                    _buffer.Add(sizeOfElement);
                    
                    // load the index onto the stack
                    for (var i = 0; i < arrayRef.rankExpressions.Count ; i++)
                    {
                        // load the multiplier factor for the term
                        _buffer.Add(OpCodes.LOAD); 
                        _buffer.Add(arrayVar.rankIndexScalerRegisterAddresses[i]);

                        // load the expression index
                        var subExpr = arrayRef.rankExpressions[i];
                        Compile(subExpr); 
                        
                        // multiply the expression index by the multiplier factor
                        _buffer.Add(OpCodes.MUL);

                        if (i > 0)
                        {
                            // and if this isn't our first, add it to the previous value
                            _buffer.Add(OpCodes.ADD);
                        }
                    }
 
                    // get the size of the element onto the stack
                    _buffer.Add(OpCodes.PUSH); // push the length
                    _buffer.Add(TypeCodes.INT);
                    _buffer.Add(0);
                    _buffer.Add(0);
                    _buffer.Add(0);
                    _buffer.Add(sizeOfElement);
                    
                    // multiply the size of the element, and the index, to get the offset into the memory
                    _buffer.Add(OpCodes.MUL);
                    
                    // load the array's ptr onto the stack, this is for the math of the offset
                    _buffer.Add(OpCodes.LOAD); 
                    _buffer.Add(arrayVar.registerAddress);

                    // add the offset to the original pointer to get the write location
                    _buffer.Add(OpCodes.ADD);
                    
                    // read, it'll find the ptr, size, and then place the data onto the stack
                    _buffer.Add(OpCodes.READ);
                    
                    // we need to inject the type-code back into the stack, since it doesn't exist in heap
                    _buffer.Add(OpCodes.BPUSH);
                    _buffer.Add(arrayVar.typeCode);

                    break;
                case VariableRefNode variableRef:
                    // emit the read from register
                    if (!_varToReg.TryGetValue(variableRef.variableName, out var compiledVar))
                    {
                        throw new Exception("compiler exception! the referenced variable has not been declared yet " +
                                            variableRef.variableName);
                    }
                    
                    _buffer.Add(OpCodes.LOAD);
                    _buffer.Add(compiledVar.registerAddress);
                    
                    break;
                case BinaryOperandExpression op:
                    
                    // TODO: At this point, we could decide _which_ add/mul method to call given the type.
                    // if both lhs and rhs are ints, then we could emit an IADD, and don't need to emit the type codes ahead of time.
                    
                    Compile(op.lhs);
                    Compile(op.rhs);

                    switch (op.operationType)
                    {
                        case OperationType.Add:
                            _buffer.Add(OpCodes.ADD);
                            break;
                        case OperationType.Mult:
                            _buffer.Add(OpCodes.MUL);
                            break;
                        case OperationType.Divide:
                            _buffer.Add(OpCodes.DIVIDE);
                            break;
                        default:
                            throw new NotImplementedException("unknown compiled op code: " + op.operationType);
                    }
                    
                    break;
                default:
                    throw new Exception("compiler: unknown expression");
            }
        }

        private static void AddPushInt(List<byte> buffer, int x)
        {
            buffer.Add(OpCodes.PUSH);
            buffer.Add(TypeCodes.INT);
            var value = BitConverter.GetBytes(x);
            for (var i = value.Length - 1; i >= 0; i--)
            {
                buffer.Add(value[i]);
            }
        }
        private static void AddPushUInt(List<byte> buffer, uint x, bool includeTypeCode=true)
        {

            if (includeTypeCode)
            {
                buffer.Add(OpCodes.PUSH);
            }
            else
            {
                buffer.Add(OpCodes.PUSH_TYPELESS);
            }
            buffer.Add(TypeCodes.INT);

            var value = BitConverter.GetBytes(x);
            for (var i = value.Length - 1; i >= 0; i--)
            {
                buffer.Add(value[i]);
            }
        }
    }
}
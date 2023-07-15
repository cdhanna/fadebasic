using DarkBasicYo.Ast;

namespace DarkBasicYo;

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

        var statement = ParseStatement();
        program.statements.Add(statement);

        program.endToken = _stream.Current;
        return program;
    }

    private IStatementNode ParseStatement()
    {
        var token = _stream.Advance();
        switch (token.type)
        {
            case LexemType.VariableInteger:

                var equalToken = _stream.Advance();
                if (equalToken.type != LexemType.OpEqual)
                {
                    throw new Exception("Parser exception! expected = statement for assignment");
                }
                
                var expr = ParseExpression();
                return new AssignmentStatement
                {
                    startToken = token,
                    endToken = _stream.Current,
                    variable = new VariableLiteralIntNode(token),
                    expression = expr,
                };
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
                    var argExpr = ParseExpression();
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
                throw new Exception("Parser exception! Unknown token for statement");
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


        return null;
    }

    private IExpressionNode ParseExpression()
    {
        var token = _stream.Advance();

        switch (token.type)
        {
            case LexemType.LiteralInt:
                return new LiteralIntExpression(token);
                break;
            default:
                throw new NotImplementedException("Expression not implemented");
        }
        
        
        return null;
    }
}
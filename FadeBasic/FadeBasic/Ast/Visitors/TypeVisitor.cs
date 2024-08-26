using System;

namespace FadeBasic.Ast.Visitors
{
    public static partial class ErrorVisitors
    {
        public static void AddTypeInfo(this ProgramNode program)
        {
            /*
             * build the type environment...
             */

            // foreach (var statement in program.statements)
            // {
            //     switch (statement)
            //     {
            //         case AssignmentStatement assignmentStatement:
            //             // get the type of the rhs...
            //             var rhs = assignmentStatement.expression.GetTypeInfo();
            //             break;
            //     }
            // }
        }

    }
}
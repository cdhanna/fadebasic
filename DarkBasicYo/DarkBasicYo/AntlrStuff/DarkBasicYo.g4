grammar DarkBasicYo;

program: statement+ EOF;

statement: printStatement
         | waitStatement
         | assignmentStatement
         | loopStatement
         | conditionalStatement;

printStatement: 'Print' STRING_LITERAL;

waitStatement: 'WaitKey' '(' ')';

assignmentStatement: VARIABLE '=' expression;

loopStatement: 'For' VARIABLE '=' expression 'To' expression
                (statement)*
                'Next' VARIABLE;

conditionalStatement: 'If' condition 'Then'
                        (statement)*
                      'EndIf';

condition: expression relationalOperator expression;

relationalOperator: '<' | '>' | '<=' | '>=' | '==' | '<>';

expression: NUMBER
          | VARIABLE
          | expression arithmeticOperator expression
          | '(' expression ')';

arithmeticOperator: '+' | '-' | '*' | '/';

VARIABLE: [a-zA-Z][a-zA-Z0-9]*;
NUMBER: [0-9]+;
STRING_LITERAL: '"' (~["\r\n])* '"';
WS: [ \t\r\n]+ -> skip;

_Fade Basic_ is a variant of BASIC. The language is fairly limited in its scope and it is intended to capture the essence of what _Dark Basic Pro_ was able to do in 2003. It is worth glancing over this document with an open mind, as some of the language decisions may raise an eyebrow in 2025. 

## Comments

On any given line of code, the <code>`</code> character turns everything to the right of the character into a _code comment_. Comments are ignored by the compiler, and allow you to mark up your code with prose.

Block comments are possible as well, using the `REMSTART` and `REMEND` keywords. Any text between those two keywords are treated as comments and ignored by the compiler. `REMSTART` and `REMEND` keywords may be nested, but it does not make much sense to do so. A `REMSTART` keyword **must** have a closing `REMEND` keyword, or the entire program after `REMSTART` will be treated as a comment.

## Variables

This is the most complete version of a variable declaration and assignment. 
```basic
LOCAL x AS INTEGER = 27
```

It has 4 main parts, 
1. `LOCAL` - this is the _scope_ of the variable. See the the [#scope](#scopes) section for more detail, but the gist is that in _Fade_, there are only 2 valid scope values, _LOCAL_, or _GLOBAL_. 
2. `x` - the name of the variable
3. `AS INTEGER` - this is the explicit type of the variable. 
4. `= 27` - the assignment that sets the value of `x`. 

That is a lot of typing! There is a shorter way to write the same statement, 
```basic
x = 27
```

In the shorter version, the variable `x` is implicitly locally scoped, and its type is inferred to be an integer. 

The _scope_ and type declaration are optional parts. Therefor, the following examples are also valid, 
```basic
LOCAL x = 1
LOCAL y
z AS INTEGER
w AS INTEGER = 2
```

----
#### Single-Line Assignment

It is possible to declare or assign multiple variables on a single line, using commas as a separator.
```basic
x = 3, y = 2
```

----
#### Sigils

Variable type inference is not as automatic as you might think.

_Fade_ variable names use [sigils](https://en.wikipedia.org/wiki/Sigil_(computer_programming)) as a way to imply their types. Sigils are appended directly to a variable name. There are only 2 types of sigils,

1. `$` - implies the variable is a string, 
2. `#` - implies the variable is a float. 

For example, the string sigil is used like this, 
```basic
message$ = "tunafish"
```

You are allowed to ignore the sigil if you explicitly include the type in the declaration. However, this is not idiomatic. 
```basic
LOCAL message AS STRING = "tunafish"
```

Otherwise, the sigil is used to infer the type, _not the value_. For example, the following statement is _invalid_. 
```basic
message$ = 27 `this is invalid, because 27 is an integer, not a string. 
```

Floating point numbers also use a sigil, (`#`), and follow the same semantics as the string sigil. For example, the following statement would declare a float. 
```basic
num# = 1.23
```

----
## Casting

Be _careful!_ Sigils are a tool intended to help you understand the types of your variables further away from their declaration. However, they can also be places where bugs appear in your logic. The float sigil particularly so, because integers and floats are implicitly cast between each other. The following statements may be surprisingly valid. 
```basic
x = 3.2 `x is an integer, and holds the value, 3
y# = 2  `y is a float, and holds the value 2.0
```

TODO: put in a section about explicit casts using `str$` and `val`

## Primitive Types

_Fade Basic_ supports the following primitives, 

| type    | byte-size | description |
| ------- | --------- | ----------- |
| `INTEGER` | 4 | a signed integer value |
| `DOUBLE INTEGER` | 8 | tba |
| `BYTE` | 1 | tba |
| `WORD` | 2 | tba |
| `DWORD` | 4 | tba |
| `BOOLEAN` | 1 | tba |
| `FLOAT` | 4 | tba |
| `DOUBLE FLOAT` | 8 | tba |
| `STRING` | 4 | tba |

----
#### Implicit Casts

Values of primitive types can usually be implicitly cast amongst each other. For example, a `FLOAT` can be implicitly cast to an `INTEGER`, or vice versa. When the sizing of the primitive types are different, the cast value is capped at the max size of the primitive value. For example, assigning the `INTEGER` value of 300 to a `BYTE` would exceed the byte's max range, and the cast value would be 255. 

## Strings

Strings are a special type of primitive that represent text. 
```basic
x$ = "hello" + " world"
PRINT x$
```

## Functions

Functions allow you to re-use sections of code. Here is an example of a function that adds two numbers together. This is the function's declaration. 
```basic
FUNCTION add(a, b)
    sum = a + b
ENDFUNCTION sum
```

Elsewhere in your program, you can invoke this function like this, 
```basic
x = add(1, 2)
```

----
#### Function Scopes

A Function cannot access variables defined outside of the Function, _unless_ those variables were declared as _GLOBAL_ scoped variables. For example, the following Function is able to access _GLOBAL_ variables, 
```basic
GLOBAL x = 3
LOCAL y = tunafish()

FUNCTION tunafish()
    result = x * 2
ENDFUNCTION result
```

----
#### Return Values

Functions can optionally return a value, or not. The following function adds two numbers together and returns the result, 
```basic
FUNCTION add(a, b)
    sum = a + b
ENDFUNCTION sum
```
And the following function does not return any result, 
```basic
FUNCTION nothing()
    ` do nothing
ENDFUNCTION
```

It is possible for a function to have multiple return statements. For example, the following function can return different values based on the logic of the function, 
```basic
FUNCTION stepFunction(x)
    IF x > 0
        EXITFUNCTION 1
        ` this line would never be executed, because it is immediately after an EXITFUNCTION
    ENDIF
ENDFUNCTION 0
```

The `EXITFUNCTION` keyword causes the function to terminate, and return the value, `1`. 

Functions **must** return the same _type_ of value on all their terminating statements. If a function returns an `INTEGER` in one spot, then it **must** also return an `INTEGER` is all other spots. Otherwise, the compiler cannot understand the inferred type of function invocations. 

----
#### Parameters

All function parameters are always passed by value. In a simple example, the following program attempts to mutate a given parameter. However, the variable is unchanged when the execution returns from the function.
```basic
x = 3
tunafish(x)
PRINT x `prints 3
FUNCTION tunafish(x)
    x = 10000
    PRINT x `prints 10000
ENDFUNCTION
```

Parameters may declare explicit types, or use [#sigils](#sigils). For example, the following function accepts a `STRING` as an input parameter, 
```basic
FUNCTION tunafish(s AS STRING)
ENDFUNCTION
```

And this function also accepts a `STRING`, but denoted through a sigil.
```basic
FUNCTION tunafish(s$)
ENDFUNCTION
```

----
#### No Nested Functions
Functions cannot be declared within another function. For example, the following program is _invalid_. 
```basic
FUNCTION outer()
    FUNCTION inner()
    ENDFUNCTION
ENDFUNCTION
```

----
#### No Lambdas or Closures
_Fade_ does not support function pointers or the ability to create a closure. 


## Scopes

A Scope is a collection of variables that are available to be accessed within the runtime of a _Fade_ program. There are only two valid scopes, _LOCAL_, and _GLOBAL_. Variables in the _GLOBAL_ scope are available everywhere, all the time. The _LOCAL_ scope changes based on which function is being executed. When a function invocation starts, a new _LOCAL_ scope is created for all of the function's parameters and variables. The previous _LOCAL_ scope is saved onto a stack. When the function invocation completes, the new scope is discarded and the old _LOCAL_ scope is rescued from the stack. 


## User Defined Types

In addition to primitives, _Fade_ supports the declaration of custom type structures, referred to as User Defined Types (UDT). The following example declares a new type and the creates a variable of the new type. 
```basic
`declare a type with two fields
TYPE TOAST
    crustyness#
    size
ENDTYPE

`declare a variable using the User Defined Type
LOCAL myToast AS TOAST
myToast.crustyness = .8
myToast.size = 12
```

The fields declared in a UDT can explicitly declare their types.
```basic
TYPE FISH
    name$ AS STRING
ENDTYPE
```

Fields can also use [#sigils](#sigils) to imply their type.
```basic
TYPE FISH
    name$ `the $ symbol implies this is a string
ENDTYPE
```

UDTs fields can be other UDTs. This allows you to create more complex structures. In this example, the `EGG` type depends on the `CHICKEN` type.
```basic
TYPE CHICKEN
    name$
ENDTYPE
TYPE EGG
    size
    chicken AS CHICKEN
ENDTYPE
```

Be careful! It is not valid to have a recursive type dependency. In the example above, it would be incorrect to add an `EGG` field to the `CHICKEN` type.

----
#### UDT Assignment

It is possible to assign a variable the value of a UDT. 
```basic
TYPE FISH
    size
ENDTYPE

x AS FISH
x.size = 3

y = x `y is implicitly a FISH
```

In the example above, `y` receives a _copy_ of the data. Any modification to `y.size` will have no effect on `x.size`. 

----
#### No Methods
_Fade_ does not support coupling functions with UDTs. 


## Arrays

Arrays are structures that hold groups of primitives or UDT data. Here is an example of an group of 10 numbers, 
```basic
GLOBAL DIM numbers(10) AS INTEGER
```

Array declarations are similar to [#variable](#variables) declarations, except that they include the `DIM` keyword and a set of parenthesis. The expression between the parenthesis define how many elements are in the array. Unlike variables which are _Locally_ scoped by default, Arrays are _Globally_ scoped by default. Arrays may also use [#sigils](#sigils) to imply their element type. 

To read or write the value of a specific item in an array, use parenthesis and then the _index_ of the value. 
```basic
DIM numbers(10)
numbers(0) = 42 `sets the first element to the value 42
```

----
#### Multidimensional Arrays

An array can have more than one element expression. In the example below, the `numbers` array has a total of _12_ elements, but laid out into 3 groups of 4. This means the array is "multidimensional". The first dimension is 3, and the second is 4. 
```basic
DIM numbers(3, 4)
```

When accessing the elements of a multidimensional array, you must provide an index value for each dimension. 
```basic
DIM numbers(3, 4)
numbers(0, 3) = 42 `sets the 4rd element in the entire array.
```

Arrays cannot have more than 5 dimensions. The following declaration is invalid. This constraint is inspired from the implementation of _Dark Basic Pro_. 
```basic
DIM numbers(1, 2, 3, 4, 5, 6)
```

----
#### Arrays of UDT

It is acceptable to declare an array that uses a UDT as the element type. In the example, `vecs` is an array that has 5 elements of the `VECTOR` UDT.
```basic
TYPE VECTOR
    x#, y#
ENDTYPE

DIM vecs(5) AS VECTOR
vecs(0).x# = 1.2 `sets the x# field on the first element
```

----
#### Array Out Of Bounds

It is invalid to access an array with an index that is less than 0, or equal-to-or-greater-than than the length of the array's dimension. If this occurs, the _Fade Basic_ program will **crash**! There is no way to recover this error during the execution of the program, so it is required that your program _avoids_ accessing an array with an invalid index. 
```basic
DIM numbers(5)
numbers(5) = 42 `this assignment causes the program to crash
```

----
#### Cannot Return Arrays From Functions

It is acceptable to create an array within the _Local_ scope of a function, but it not valid to return the array from the function. 

----
#### Cannot Assign an Array

It is not valid to implicitly assign an entire array. For example, this program is not valid.
```basic
DIM fish(5)
DIM sticks(5)

fish = sticks `this assignment is not valid
```

## Literals

There are 4 ways to type numbers in _Fade Basic_. Other than the default base-10 decimal system, numbers may be prefixed with a symbol that denotes their base.

| Name | Base | Symbol |
| ---- | ---- | ------ |
| Decimal | 10 | |
| Binary | 2 | `%` |
| Octal | 8 | `0c` |
| Hexadecimal | 16 | `0x` |

All of these assignments create the same value, but use different methods to express the value.
```basic
x = 52 `decimal, base 10
y = %110100 `binary, base 1
z = 0c64 `octal, base 8
w = 0x34 `hex, base 16
```


## Operations

You can perform mathematical operations between variables. Operations can be grouped using parenthesis.
```basic
x = (3 + 1) * 4 `results in 16
y = 3 + (1 * 4) `results in 12
```

----
#### Numeric Operations

Most numeric operations require two expressions, a _left_ and _right_. The follow table shows the available numeric operations between two expressions. 

| Operation | Description |
| --------- | ----------- |
| + | adds two numbers <pre>1 + 2 `3 </pre> |
| - | subtracts the right number from the left <pre>4 - 1 `3 </pre> |
| * | multiplies two numbers <pre>2 * 3 `6 </pre> |
| / | divides the left number by the right <pre>6 / 3 `2 </pre> |
| mod | takes the modulo of the left number by the right <pre>4 mod 3 `1 </pre> |
| ^ | raises the left number to the power of the right <pre>2 ^ 3 `8 </pre> |
| AND | results in `1` if both left and right values are positive. Otherwise, the result is `0` <pre>1 AND 2 `1 </pre> |
| OR | results in `1` if either the left or right values are positive. Otherwise, the result is `0` <pre>1 OR 0 `1 </pre> |
| XOR | results in `1` if either the left or right values are positive, but not both. Otherwise, the result is `0` <pre>1 XOR 1 `0 </pre> |
| > | results in `1` if the left number is greater than the right number. Otherwise, the result is `0`  <pre>2 > 1 `1 </pre> |
| < | results in `1` if the right number is greater than the left number. Otherwise, the result is `0`  <pre>1 < 2 `1 </pre> |
| >= | results in `1` if the left number is greater than _or equal_ to the right number. Otherwise, the result is `0` <pre>2 >= 2 `1 </pre> |
| <= | results in `1` if the right number is greater than _or equal_ to the left number. Otherwise, the result is `0` <pre>2 <= 2 `1 </pre> |
| = | results in `1` if the left number is equal to the right number. Otherwise, the result is `0` <pre>2 = 2 `1 </pre> |
| <> | results in `1` if the left number is _not_ equal to the right number. Otherwise, the result is `0` <pre>2 <> 2 `0 </pre> |
| >> | _Bitwise_; right shifts the left by the right <pre>4 >> 1 `2 </pre> |
| << | _Bitwise_; left shifts the left by the right <pre>1 << 2 `4 </pre> |
| ~~ | _Bitwise_; results in the XOR between the left and right  <pre>4 ~~ 2 `6 </pre>|
| .. | _Bitwise_; results in a number the bitwise opposite of the left <pre>4..0 `-5 </pre> |
| || | _Bitwise_; results in the OR between the left and right <pre>4 || 2 `6 </pre>|
| && | _Bitwise_; results in the AND between the left and right <pre>5 && 3 `1 </pre> |

When performing operations with numeric variable, variables will be implicitly cast if needed. 

There is also a few unary numeric operations that only require a single number. 
| Operation | Description |
| --------- | ----------- |
| NOT | if the number is not `0`, the result is `0`. Otherwise, the result is `1` |
| - | negate the numeric value |
| .. | _Bitwise_; results in a number the bitwise opposite of the numeric value |


----
#### Commands

_Fade Basic_ uses _Commands_ to do most of the interesting work a program will do. _Commands_ are like [#functions](#functions), except that they are declared in `C#`. All _Fade_ programs need to specify which _Commands_ they will use ahead of time. You can write yor own _Commands_, or use the standard off the shelf ones for now while _Fade_ is still in development. 

_Commands_ can pretty much do anything, and they take the place of any built in language standard library. 
The `PRINT` command is a very common one to use, available in the `FadeBasic.Lib.Standard.ConsoleCommands` collection. 

```basic
PRINT "hello world"
```

_Commands_ may accept parameters, and may return a value. For example, the `CONSOLE WIDTH()` command _returns_ a value.
```basic
width = CONSOLE WIDTH()
```

When a command has a return value, it must be invoked using parenthesis. Notice that the `PRINT` command is invocable without parenthesis, because it does not return a value. However, the `CONSOLE WIDTH()` command returns a number, and therefore requires parenthesis. 

When a command requires multiple parameters, they may be optionally separated with commas. Both of these statements are valid.
```basic
SET CURSOR 1 2
SET CURSOR 1, 2
```

Commands are very specific to your given project, based on which are configured in the `.csproj` file. Commands are grouped into _collections_. To use a command collection, you need to do two things, 
1. Reference the project or Nuget package in your `.csproj` file, 
2. Include a `<FadeCommand>` reference.

Here is an example of a `.csproj` file that uses two command collections,
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net8.0</TargetFramework>
    <OutputType>Exe</OutputType>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FadeBasic.Build" Version="0.0.18.1" />
    <PackageReference Include="FadeBasic.Lang.Core" Version="0.0.18.1" />
    <PackageReference Include="FadeBasic.Lib.Standard" Version="0.0.18.1" />
    
    <FadeCommand Include="FadeBasic.Lib.Standard" FullName="FadeBasic.Lib.Standard.ConsoleCommands" />
    <FadeCommand Include="FadeBasic.Lib.Standard" FullName="FadeBasic.Lib.Standard.StandardCommands" />
    <FadeSource Include="main.fbasic" />
  </ItemGroup>
</Project>
```

Commands provide their own documentation, so look for the documentation from the author of the command collection. The `FadeBasic.Lib.Standard` collection has documentation available here, [Standard Library](https://github.com/cdhanna/fadebasic/blob/main/FadeBasic/book/FadeBook/Standard%20Library.md).

To build your own command collection, check the [Custom Commands Documentation](https://github.com/cdhanna/fadebasic/blob/main/FadeBasic/book/FadeBook/Custom%20Commands.md).

----
#### Short Circuiting 
TODO

## Control Statements

Control statements specify the flow of execution in the program. 

----
#### Conditionals

The `IF` statement allows the program to optionally run statements. 
```basic
x = 3
IF x > 1
    PRINT "true"
ELSE
    PRINT "false"
ENDIF
```

The `IF` keyword must be followed by a numeric expression. When the expression results in a _positive_ number, then the expression is truthy and the conditional's truthy statements will execute. 

Optionally, the `ELSE` keyword may be used to define statements that should run with the expression is _not_ truthy. Only one set of statements will execute per conditional. 

Once the statements are done executing, the program will resume at the `ENDIF` keyword. The `ENDIF` keyword is required for multi-line conditionals. 

Conditionals have a single line variant that uses the `THEN` keyword. When the `THEN` keyword is used, the `ENDIF` keyword must not be used.
```basic
x = 3
IF x > 0 THEN PRINT "true" ELSE PRINT "false"
```


----
#### Single Line Statements

Although unadvisable, the spirit of _Dark Basic Pro_ empowers _Fade Basic_ to run multiple statements on a single "line" of code. 
```basic
x = 3
IF x > 0 THEN PRINT "tuna" : PRINT "fish"
```

----
#### For Loops

The `FOR` statement allows the program to repeat a block of code a specific number of times. 
```basic
FOR t = 1 TO 10 STEP 1
    `whatever code is in here will run 10 times. 
NEXT
```

The `FOR` statement has 4 main components, 
1. The variable declaration, `t = 1`, 
2. The exit condition, `TO 10`, 
3. The `STEP` amount, which is `1`. This clause is optional, and defaults to 1. 
4. The looping statement, `NEXT`. 

The declaration sets a new variable, or an existing variable to the given value. This is called the _control variable_. The statements within the `FOR` statement are executed. Then the control variable is incremented by the `STEP` value. If the variable's value is less than _or equal to_ the `TO` value, the `FOR` statements are executed again. This process repeats until the value in the control variable is _greater_ than the `TO` value. 

The following code will print "1", "2", and "3".
```basic
FOR t = 1 TO 3
    PRINT t
NEXT
```

It is possible to create a `STEP` value that is negative. This example will print "3", "2", and "1". 
```basic
FOR t = 3 TO 1 STEP -1
    PRINT t
NEXT
```

Every `FOR` statement must include the `TO` expression, and must close with a `NEXT` keyword.

It is possible to exit early from a `FOR` loop using the `EXIT` statement. The `EXIT` statement will skip any modifications to the control variable move the program execution to the end of the loop.
```basic
FOR t = 1 TO 10
    EXIT
NEXT
PRINT t `prints 1
```

----
#### While Loops

The `WHILE` statement allows the program to repeat a block of code until a certain condition is truthy. 
```basic
x = 3
WHILE x > 0 
    `whatever code is in here will run until x is not greater than zero
ENDWHILE
```

The `WHILE` keyword must be followed by a numeric expression. When the expression results in a _positive_ number, then the expression is truthy and the inner statements will execute. Every `WHILE` statement needs a closing `ENDWHILE`. When the `ENDWHILE` keyword is reached, the program execution re-runs the conditional expression, and if it is truthy, then execution loops back to the start of the while loop.

If the conditional expression is never truthy, then the inner statements are never executed. 

It is possible to exit early from a `WHILE` loop using the `EXIT` statement. The `EXIT` statement ignores the conditional expression, and moves the program to the end of the loop.
```basic
WHILE 1
    EXIT `causes the code to not run forever
ENDWHILE
```

---- 
#### Repeat Loops

The `REPEAT` statement is similar to the [#While](#while-loops) statement, except that the conditional evaluation happens at the end of the loop instead of the beginning. 
```basic
REPEAT
    `whatever code is in here will run until x is not greater than zero
UNTIL x > 0
```

Every `REPEAT` keyword needs a closing `UNTIL` keyword. The `UNTIL` keyword must be followed by a numeric expression. When the expression results in a _positive_ number, then the expression is truthy and the inner statements will execute again.

If the conditional expression is never truthy, then the inner statements will not be executed a second time.  

It is possible to exit early from a `REPEAT` loop using the `EXIT` statement. The `EXIT` statement ignores the conditional expression, and moves the program to the end of the loop.
```basic
REPEAT
    EXIT `causes the code to not run forever
UNTIL 1
```

---- 
#### Do Loops

The `DO` loop will execute a block of code forever. 
```basic
DO
    `whatever code is here will run forever
LOOP
```

Every `DO` keyword must have a closing `LOOP` keyword. When the program reaches the `LOOP` keyword, the program returns execution to the start of the loop. 

The usual way to exit a `DO` loop is to use the `EXIT` keyword, which will move the program's execution to the end of the loop.
```basic
DO
    EXIT
LOOP
```

----
#### End

The `END` statement immediately stops the program. 
```basic
END
PRINT "Where did everyone go?" `this line will never be printed
```

This statement is a powerful tool to stop your program before running into labels or functions that should not be executed. However, there is no _need_ to use the command if your program would naturally exit.

---- 
#### Goto

The `GOTO` statement will jump the execution of the program to a specific label in your source code.
```basic
x = 5

tuna:
x = x - 1

IF x > 0 THEN GOTO tuna
```

`GOTO` statements can be used to escape from looping control structures, or indeed from any control statements. However, `GOTO` statements _cannot_ be used to jump the code between [#scopes](#scopes).

_Labels_ are defined as any valid variable name, with a `:` symbol immediately following the name. Labels cannot be redeclared.

----
#### GoSub

The `GOSUB` statement is similar to [#Goto](#goto), except that execution can be returned to the `GOSUB` statement.
```basic
LOCAL x
GOSUB tuna:
PRINT x `prints 1
END


tuna:
x = 1
RETURN
```

----
#### Select Statements

The `SELECT` statement can branch code execution into several paths based on a numeric expression.
```basic
x = 1
SELECT x
    CASE 0
        PRINT "zero"
    ENDCASE
    CASE 1
        PRINT "one"
    ENDCASE
    CASE DEFAULT
        PRINT "default"
    ENDCASE
ENDSELECT
```

The `SELECT` keyword must be followed by a numeric expression. This expression is called the control value.
A `SELECT` statement is made up of many `CASE` statements. The `CASE` keyword must be followed by a constant numeric literal. Each `CASE` statement has a set of inner statements that will only be executed if it is equal to the control value. Each `SELECT` statement is allowed one special `CASE` statement that has the keyword, `DEFAULT` instead of a numeric literal. If none of the cases' values match the control value, then the `DEFAULT` case is selected. If no case is selected, the program execution moves onto the closing `ENDSELECT` keyword. 



## Macros
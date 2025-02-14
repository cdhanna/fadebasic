# Dark Basic Pro Changes

_Fade Basic_ was heavily inspired from _Dark Basic Pro_, created by _The 
Game Creators_. Most of _Fade Basic_ is directly copied from _Dark Basic 
Pro_. However, there are a few critical differences between the two 
languages. If you are familiar with _Dark Basic Pro_, please read the 
following change list so that you are aware of behavioral differences. 

If you are not familiar with _Dark Basic Pro_, then read the [Language Guide](https://github.com/cdhanna/fadebasic/blob/main/FadeBasic/book/FadeBook/Language.md)


## More Compile Time Checks

_Fade Basic_ does _more_ type and safety checking at compile time than _Dark Basic_. In many situations, _Dark Basic_ would throw runtime errors and crash. _Most_ (but not all!) of those situations result in compile time errors in _Fade Basic_. 

The two cases where _Fade_ still throws runtime errors are index-out-of-bounds errors during array access, and divide-by-zero errors. 

----
#### No Jumping Between Scopes

This program is invalid in _Fade Basic_, and an error appears on the `GOTO` statement. 
```basic
GOTO label

FUNCTION fishSticker(a)
    label:
ENDFUNCTION a + 1
```

It is an invalid program because you cannot use a `GOTO` or a `GOSUB` to jump between scopes. The program is ill-defined if you could. There would be no clear value for the variable `a`, and no clear directive for the return value, `a + 1`. 

----
#### No Exploding Functions

In _Dark Basic_, it was required that your program's execution never encounter the declaration of a function. The following program would explode if executed in _Dark Basic_. 
```basic
x = 3

` when execution reaches this line, DBP explodes.
FUNCTION kaboom()
ENDFUNCTION
```

This runtime error no longer happens, and it is considered valid code in _Fade Basic_. There is no compile error. 

----
#### Type Inference

It was possible to "confuse" _Dark Basic Pro_'s compiler in various ways regarding misaligned types. _Fade Basic_ sports a type inference system that will report compile errors if the inferred types of expressions are not compatible. 


## No Pointers

_Dark Basic Pro_ supported the ability to create a pointer from a variable, but it did not support the ability to _dereference_ a pointer back to a variable. To be honest, I'm not sure how pointers were meant to be used if you could only create pointers, and never dereference them. As such, I left their implementation absent completely. In _Fade Basic_, you cannot create a pointer. 

## No #INCLUDE

In _Dark Basic Pro_, you could use the `#INCLUDE` pre-compiler symbol to pull inline additional code files. In _Fade Basic_ it is possible to construct a program from multiple source files, but it must be done using the `<FadeSource>` node in the `csproj` file. Refer to the [Project Guide](https://github.com/cdhanna/fadebasic/blob/main/FadeBasic/book/FadeBook/Projects.md) for more information.

## The Commands 

_Dark Basic Pro_ came with "batteries included". There were a wealth of 2D and 3D graphical commands for drawing sprites, shapes, and playing sounds, etc. _Fade Basic_ takes inspiration from those commands in its standard library. _Most_ of the "CORE" commands from _Dark Basic Pro_ are part of the Standard Library command collection. However, in _Fade_, all command collections are opt-in, and must be registered in the `csproj` file. Refer to the [Project Guide](https://github.com/cdhanna/fadebasic/blob/main/FadeBasic/book/FadeBook/Projects.md) for more information.

## Return User Defined Types from Functions

In _Dark Basic Pro_, it was invalid to return a UDT instance from a function. This is valid in _Fade Basic_. 

```basic
TYPE EGG
    color$
ENDTYPE

egg = getEgg() `this is valid.

FUNCTION getEgg()
    egg AS EGG
    egg.color$ = "red"
ENDFUNCTION egg
```

## No String Concatenation with semicolons
_Fade Basic_ does not support string concatenation with the `;` character. Instead, please use the `+` character. 

## Globals In Functions
In _Dark Basic Pro_, if a function declared a globally scoped variable, then the value was reset every time the function ran. However, in _Fade Basic_, the declaration does not reset the value.

```basic

tunaSticks() `prints 1
tunaSticks() `prints 2

FUNCTION tunaSticks()
    GLOBAL x
    x = x + 1
    PRINT x
ENDFUNCTION
```

However, if the declaration includes an initializer expression, then it _is_ reset on every invocation. 
# The C# backend

A second backend for Fade: instead of bytecode for the VM, it emits a C# class that the
project's normal C# build compiles. The VM stays the reference implementation, and the
backend's job is to behave exactly like it, quirks included.

## Using it

Set `<FadeBackend>CSharp</FadeBackend>` in a Fade project. The build task then writes the
C# program into the same generated file as the launchable (`FadeGeneratedLaunchFile`,
`GeneratedFade.g.cs` by default), which implements `ICompiledLaunchable`. A CSharp build
carries no bytecode and no bytecode debug data, so everything runs compiled:

| how it is started                       | what runs                                          |
|-----------------------------------------|----------------------------------------------------|
| `dotnet run`                            | the compiled program                               |
| `--fade-test*`, `dotnet test`           | the compiled tests (runto, mocks, asserts, defers) |
| `--fade-watch`, the Fade debugger (DAP) | not available: it says so and runs compiled        |

Build with `-p:FadeBackend=VM` (the default) for the VM, hot reload and the Fade debugger.
`FadeIncludeTests=false` (the default outside Debug) leaves test blocks out of both
backends' output.

If the backend can't compile a program faithfully, the build fails with `FADE:CS0001` and
says what it refused.

`CSharpBackendDemo/` is a console project wired to this repo's build task:
`dotnet run --project FadeBasic/CSharpBackendDemo` (add `-p:FadeBackend=VM` to compare).

## Shape of the generated code

- One class deriving `FadeBasic.Compiled.CompiledFadeProgram`. Fade `type`s become nested
  C# structs; global-scope registers become fields (`g<register>_<name>`), function-local
  registers become C# locals (`l<register>_<name>`). Register numbers are the VM's, because
  the transpiler drives the same `CompileScope` in the same order as `Compiler`.
- Main is `__Main()`, each Fade function is a method `F_<name>`. A gosub routine that
  nothing can fall or jump into is its own method `G_<label>` (see
  `CSharpTranspiler.Routines.cs`), which keeps every method under the JIT's optimizing size.
- A method with no `goto` (and no gosub to a routine left inline) is structured C#:
  `if`/`else`, `while`, `break`, `continue`. A method with one stays flat, labels and
  `goto` mirroring the bytecode's jumps, so a jump into a loop is still exact.
- Comparisons used as conditions are plain C# conditions (`if (a < b)`); as values they are
  the VM's 0/1.
- Commands are direct static calls to their C# methods (found from the generated executor),
  through `using` aliases. `[FromVm]` arguments receive a host-context VM whose
  `instructionIndex` is set to a unique call-site id (`FadeRt.At`), which keeps debug-UI
  widget identities stable.
- Each method body is one `unchecked { }` block: Fade arithmetic wraps.
- With a source map, every statement gets a `#line` directive pointing at its `.fbasic`
  line; the file name is only repeated when it changes.
- A function nothing calls is compiled (for its checks and register numbers) but not
  written out.

### Suspension (`sync`)

A function that can reach a suspending command (by default, any command that takes the VM)
is a C# iterator; so is `__Main`. A call to one is `yield return F_x(...)`, and after a
suspending command the code checks `__vm.isSuspendRequested` and does `yield return null`.
`CompiledFadeProgram.Run()` drives the iterators with an explicit stack, so resuming costs
the same at any depth. Expressions that can suspend are lowered to one temp per step so a
yield can sit between any two evaluation steps. Everything else is a plain method.

## VM semantics that are reproduced

- Type promotion: the higher `ORDER_PREC` wins, a tie keeps the left type (`int + dint` is
  `int`). Subtraction is `a + (b * -1)`, negation is `* -1`, so `byte - byte` is `int`.
- Conditions are `CAST INT` then `> 0`: negatives and `0.5` are false.
- `a or b` / `a and b` short-circuit and keep the left value (`5 or 0` is 5).
- `for`: the start expression is re-evaluated every iteration, the bounds are sorted at
  runtime, and the loop runs while `min <= x <= max` (`for i = 3 to 1` runs once).
- String `>`/`>=` use `CompareOrdinal == 1` / `!= -1`; `=` on `double float` is always false.
- `select` compares raw register bits, later cases win ties, and strings match by identity
  (only the same interned literal).
- Division by zero is an error for floats too; `mod` by zero throws `DivideByZeroException`.
- Function return values are not cast to a declared type: the transpiler infers each
  function's runtime return type (fixed point over the program) and refuses functions that
  return different types on different paths.
- `params` arguments are evaluated right-to-left and passed uncast (bytes reinterpreted).
- `ref` arguments on registers round-trip like the VM's register write-back (only the low
  32 bits survive; types shorter than 4 bytes throw).
- Array-to-array assignment copies the data but keeps the target's rank sizes.

## Known differences

- An unassigned string array element / struct string field is heap pointer 0 in the VM,
  which reads whatever allocation lives there. The backend reads `""`.
- Fresh struct allocations in the VM may reuse freed, unzeroed memory; the backend's are zero.
- `runto ... max cycles` counts VM instructions, which compiled code does not have; it is refused.
- `RawArg` commands are refused.

## Tests

`Tests/CSharpBackend/`: every program literal from the VM test files plus a set of
semantics programs runs on both backends; output, runtime errors, suspension counts and
every global register (scalars, strings, structs, arrays) must match.
`CSharpBackendPerf` (explicit) times both backends.

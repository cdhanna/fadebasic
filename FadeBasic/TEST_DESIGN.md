# Fade Test Design

A design sketch for first-class testing in Fade. Companion to [TESTS.md](TESTS.md), which covers the `dotnet test` integration plumbing. This doc covers the **language-level** test model: what tests look like in `.fbasic` source.

## The core insight

Conventional test frameworks (xUnit, NUnit, MSTest) assume the host language has a clear separation between *declarations* (functions, types) and *execution* (a `Main`). Tests work by calling functions in isolation. Fade, like classic BASIC, has no such separation — the program *is* the imperative top-level body. There is no `Main` to skip, and most interesting behavior lives inside the main loop, not in factor-out-able functions.

A test framework that grafts xUnit's model onto Fade ends up either:
- forcing users to refactor everything into testable functions (fights the language), or
- creating a separate "test scope" with awkward bridges to program state (the scope-access problem).

The fresh paradigm: **a test is a script that drives the program between its natural pause points.**

The program's labels (`:start`, `:sync`) become cue points. A test pauses execution at a label, optionally pokes state, advances to the next label, and asserts. These are ordinary language labels — the same kind used by `goto`/`gosub` — not a test-only construct, so adding tests to Fade requires no new label machinery and labels carry no runtime cost. Scope merges automatically because the test is *inside* the paused program — it's a debugger session expressed as code.

## Headline syntax

```fbasic
` --- program ---

imageId = 1
texture imageId, "fish.png"

x = 0
y = 0
sprite 1, x, y, imageId

:start
sync rate 60
do
    :step
    x = x + 1
    set sprite position 1, x, y
    if x > screen width()
        x = 0
    endif
    :sync
    sync
loop

` --- tests ---

abstract test root
    local fakeWidth = 10
    mock screen width
        returns fakeWidth
    endmock
endtest

test wraps_at_right_edge from root
    runto :start
    assert x is 0

    for n = 1 to fakeWidth + 1
        runto :sync
    next

    assert x is 0    ` should have wrapped exactly once
endtest

test moves_one_per_frame from root
    runto :start
    local before = x
    runto :sync
    assert x is before + 1
endtest
```

## Design principles

### 1. Test is a block that captures a token stream into the test manifest

`test name ... endtest` is a top-level construct. Not `#test` decorating a function. This:
- Eliminates the "test scope vs function scope" tension.
- Lets the test body refer to program-scope identifiers directly.
- Removes the awkward "register the function" step.

`test` implicitly opens a tokenize-flavored region whose tokens route to the **test manifest**, not back into program code. No `#tokenize` keyword required — `test` knows. In `dotnet run` builds, test blocks compile to nothing — there is no manifest. In `dotnet test` builds, each block contributes one entry.

**Tests compose with `#macro`.** A `test` block can sit inside a `#macro` and consume its compile-time values via `[name]` substitution, exactly like a `#tokenize` region:

```fbasic
#macro
    for n = 1 to 5
        test "addCorrectly_" + [n]
            assert add([n], [n]) is [n] * 2
        endtest
    next
#endmacro
```

This is *not* nested macros. `test` is a tokenize-flavored region, not a macro itself, so it's legal inside `#macro`. What's still illegal: opening a new `#macro` block inside a test that's already inside `#macro`. If a test inside an explicit `#macro` needs compile-time setup, just put it in the surrounding macro before the `test`.

**Top-level `test` blocks have a hoist rule.** When a test sits at top level (not inside an explicit `#macro`), the compiler synthesizes a `#macro` wrapper around it. If the author writes a `#macro ... #endmacro` block inside the test body, those statements are hoisted out of the test and into the synthesized wrapper:

```fbasic
` source
test a
    #macro
        x = 23
    #endmacro
    assert [x] is 23
endtest

` desugared
#macro
    x = 23
    test a
        assert [x] is 23
    endtest
#endmacro
```

This preserves the no-nested-macros rule (the inner `#macro` was notational; it never really nests) while letting authors write per-test compile-time setup locally.

### 2. Fixtures are tests you continue from

`test B from A` runs A's body, then continues from where A left off into B's body. No separate fixture concept. A test with only setup is a fixture in spirit; mark it `abstract test` to declare "do not run on its own, only continue from."

The semantic emphasis is *continuation*, not classical inheritance. Child B doesn't inherit definitions and re-execute fresh — it picks up from A's exact ending state, including any program execution A drove (mocks installed, `runto`s already taken, program counter wherever A paused it).

Composition rules:
- The parent's `local` declarations and test-functions are visible to the child.
- The parent's mock queues are present at the child's start (since the parent's body runs first and installs them). The child can append further entries, or `clear mock` to start fresh.
- Single-parent only for now.

Two valid implementation strategies, with the same observable semantics:

- **Replay.** Each child run starts fresh and re-executes its parent chain top-down before running the child body. Simple, robust, slow as the chain grows.
- **Snapshot.** After a parent finishes, take a snapshot of VM state (and any mockable C# host state). Child runs start from the snapshot. Faster for deep trees with shared expensive setup; harder to implement because the snapshot has to capture everything reachable — VM stack, heap, mock table, host-side bindings — and restore it perfectly.

Replay is the right default. Snapshot is an optimization for later, gated on a perf measurement that says it matters. The contract should be defined in terms of observable behavior ("child sees parent's ending state") so either implementation is valid.

### 3. Mocks are FIFO queues of behaviors

```fbasic
mock screen width
    returns fakeWidth
endmock
```

A `mock` block configures behavior for a Fade command at the C# boundary. The simple form above means *"every call to `screen width` returns `fakeWidth`, for the rest of the test."*

**Mocks are queues, not single overrides.** The body of a `mock` block is an ordered list of behavior entries. Each call to the command consumes from the front of the queue (FIFO). Frequency words on each entry control how many calls that entry serves:

```fbasic
mock screen width
    returns 10 once
    returns 20 once
    returns 5 always
endmock
```

- Call 1 → `10`
- Call 2 → `20`
- Call 3 onwards → `5`

Frequency words: `once`, `n times`, `always`. Default (no frequency word) is `always` — the entry stays in the queue forever and serves every call until the queue is reconfigured. Anything beyond an `always` entry is unreachable and produces a build warning.

**Behavior entries:**

- `returns <expr>` — push the value of `<expr>` as the command's return. `<expr>` is any Fade expression evaluated in the test's scope; it can reference `local`s, mocks, captured globals (post-`runto`), test-functions.
- `forbid` — calling the command at this point fails the test with *"command `screen width` was forbidden by mock at line N."* Useful as a strict-mode terminator: *"after the first call returns 10, no more calls are allowed."*

That's the v1 surface. Two behaviors plus three frequency words.

**Exhausted queue → real implementation.** When a command is invoked and the queue is empty (either never set up or fully consumed), the real C# implementation runs. Mocks are an *override*, not a *requirement*. This composes with the rest of the design: math commands and other side-effect-free utilities just work without explicit mocking. Tests that need strict guards add `forbid` as a final entry.

**Multi-block mocks append.** Re-declaring `mock <command>` in the same test adds entries to the existing queue rather than replacing it. To wipe and start over:

```fbasic
clear mock screen width    ` empty the queue for one command
clear mocks                ` empty all queues
```

This is mostly useful between phases of a long test or when overriding a `from`-parent's mocks.

**All overloads share one queue.** A `mock screen width` configures the queue for every overload of `screen width(...)`. Argument-based dispatch is deferred to a later phase.

**Per-test isolation.** The mock table lives on the VM. Since each test gets a fresh VM instance, mock state is naturally isolated — no leakage between tests.

**Mechanism.** When a command-invocation OPCODE fires, the dispatcher consults the mock table first:

```
on command_invoke(cmd_id, args):
    queue = mockTable[cmd_id]
    if queue.empty:
        invoke_real_implementation(cmd_id, args)
    else:
        entry = queue.peek()
        match entry:
            returns expr → push value of expr; decrement-or-pop
            forbid       → fail test
```

Queue entries hold a remaining-uses counter; `once` is `1`, `n times` is `n`, `always` is `infinity`. Decrement-or-pop drops the entry when its counter hits zero (except `always`, which is never popped).

### 4. `runto :label` drives the program

The fundamental time-control primitive. Its name echoes `gosub` / `goto` so the label-targeted nature reads at a glance: "go [forward] until you hit this label." The test pauses at the label; the test body then executes assertions or mutations; another `runto` advances further.

Two forms — a simple inline form and a block form for additional constraints:

```fbasic
` simple form
runto :sync

` block form — extensible
runto :sync
    max cycles 1000
endrunto
```

The block form opens the door to future conditions without breaking the simple case. `max cycles N` budgets the number of VM cycles before the test fails — a guard against runaway loops. (Counting VM cycles, not frames, lets the same budget mean the same thing whether the program is in a tight inner loop or rendering at 60 fps.)

Mocks and `local` declarations can precede the first `runto`. Before the first `runto`, no program top-level code has executed yet — globals declared with `global X = ...` are present at their initial values, but names introduced only by main-body assignments (e.g., bare `x = 5` at top level) are not yet bound and referencing them is an error. This means a typical test reads as: setup mocks → first `runto` enters the program → script execution forward through labels → assert.

**Targets are any label.** `runto :L` is valid for any label, top-level or inside a function. Stepping into a function and asserting about its state is a first-class use case — tests aren't limited to driving the main loop.

**Resume is stack-agnostic.** When the program is paused (top-level or mid-function) and the test issues a `runto`, the VM resumes execution as-is from wherever the program was. The program's call stack is honored — functions return naturally, gosubs resolve naturally, the test doesn't reach into the stack to unwind anything. `RUNTO_YIELD` fires whenever the program's IP reaches the target, regardless of how deep the call stack is at that moment. The `max cycles` clause guards against runaway loops or programs that never reach the target.

### 5. Scope merges at the pause point

Inside a test block, after a `runto`, identifier resolution is **read-through, write-through** to the paused program's scope:

- `x` reads the program's x.
- `x = 5` mutates the program's x.
- `local foo = 10` declares a test-only name. New names without `local` also become test-locals; `local` is the explicit, documented form.

Mental model: it's exactly what you'd type into a debugger console at a breakpoint. The test should feel the same.

**Strict semantic.** The visible scope after `runto :L` is exactly the set of names that would be in scope at line `:L` if the test code were spliced in there. This is computed statically via a `scope_at(:L)` map.

For a top-level label, that's *globals + any name declared by main-body execution up to `:L`*. For a label inside a function, that's *the function's parameters + locals declared up to `:L`, plus globals visible at the function's callsites* — exactly what Fade's existing scope checker already computes for any line of any function. The test scope query just asks "what's in scope at this address?" and reuses the answer.

The semantic is *as if* the test body were spliced into the program at each `runto` point. So this fails type-check:

```fbasic
x = 12
:label
x = x + 1
:later
y = 12

test example
    runto :label
    assert y is x       ` ERROR: y not in scope_at(:label)
endtest
```

Because spliced in:

```fbasic
x = 12
:label
assert y is x           ` y is unknown here — only declared after :later
x = x + 1
:later
y = 12
```

After a second `runto :later`, the visible set updates to `scope_at(:later)`, and `y` becomes visible. Each distinct runto target carries its own scope.

**Branch rule for declarations.** Following Fade's existing semantics, names introduced in any branch of a top-level `if/else` are considered declared at the merge point — even if a runtime branch could have skipped them. Their value defaults to zero/empty if the assigning branch didn't execute. Example: after `if condition then ta = 3 else tb = 4`, both `ta` and `tb` are declared and visible regardless of which branch ran.

**Pre-runto function calls.** Calling a program function from a test before any `runto` reuses Fade's existing function-callsite analysis: the function's transitively-read names must all be declared at the callsite. If the program top-level body hasn't run yet, names declared only by main-body assignments aren't yet present, and the test gets the same parse-time error a regular Fade program would for calling a function ahead of its dependencies. No new machinery — the test's `visible` set is just another callsite snapshot fed to the existing check.

### 6. Tests can declare their own functions

Tests can define functions for code reuse, scoped just like `local` variables:

```fbasic
abstract test root
    local fakeWidth = 10
    mock screen width
        returns fakeWidth
    endmock

    function expect_in_bounds()
        assert x >= 0
        assert x <= fakeWidth
    endfunction
endtest

test wraps_at_right_edge from root
    runto :start
    expect_in_bounds()              ` carried forward from root
    for n = 1 to fakeWidth + 1
        runto :sync
        expect_in_bounds()
    next
endtest
```

Rules:
- A function declared inside a test is visible only within that test and any test that continues `from` it.
- The `from`-chain carries functions forward, the same way it carries `local` variables and mocks.
- A test-scoped function can call program-scope functions normally, but cannot redeclare one (use a different name).
- Test functions can do everything regular Fade functions can — including `assert`, since they only execute inside a test context.

### 7. Label namespaces are walled off

Tests can declare their own labels (e.g., `:retry`) and `goto` / `gosub` them freely. They **cannot** `goto` or `gosub` into a program label — that's strictly the job of `runto`.

```fbasic
test retries_three_times from root
    local attempts = 0
    :retry                           ` test-local label — fine
    runto :sync
    attempts = attempts + 1
    if attempts < 3 then goto :retry ` jumping within the test — fine

    ` goto :start                    ` would be an error — :start is a program label
    runto :start                     ` correct way to advance to a program label
endtest
```

Rules:
- `goto` / `gosub` inside a test resolve only against test-local labels.
- Targeting a program label with `goto` / `gosub` is a parse-time error — the resolver doesn't fall through.
- `runto` is the only construct that crosses from test scope into program scope.
- Symmetrically, program code can't `goto` or `gosub` into a test — but that's already true since test blocks compile to nothing in run builds.

This namespace separation means tests can never accidentally jump into the middle of program execution. Every entry into program code is explicit and label-targeted.

### 8. Compiler foothold

The model maps cleanly onto the existing parser/checker architecture:

- **Lex/macro pass.** `test ... endtest` is recognized as a tokenize-flavored region during macro expansion, alongside `#tokenize ... #endtokenize`. Tokens emitted from inside a `test` body are routed to the test manifest, not back into the program token stream. Two desugaring rules apply at this stage:
  1. A top-level `test` (not inside an explicit `#macro`) is wrapped in a synthesized `#macro` block.
  2. Inner `#macro ... #endmacro` blocks written inside a top-level `test` body are hoisted out of the test and into that synthesized wrapper, preserving the no-nested-macros invariant.
- **Parsing.** A new `TestNode` (parallel to `FunctionStatement`) joins `ProgramNode` as a top-level form. Each `TestNode` carries its own `labels`, `functions`, and statement body — same shape as a function. After macro expansion, parameterized tests have already been unrolled into N distinct `TestNode`s with `[name]` substitutions resolved.
- **Scope.** Each test gets its own `Scope`, much like a function does today. The test's scope holds test-local variables, test-local functions, and test-local labels. Identifier resolution after a `runto :L` consults a `scope_at(L)` snapshot — the set of names that would be in scope at line `:L` if you wrote regular Fade code there. For top-level labels, that's globals plus any main-body declaration up to `:L`. For function-internal labels, it's the function's parameters and locals declared up to `:L`, plus globals visible at the function's callsites. Both cases are answered by reusing the existing `ScopeErrorVisitor` result — it already computes what's in scope at every line; tests just query it for runto targets.
- **Checks added to `ScopeErrorVisitor`.** Three new validations on top of the existing `labelTable` / `functionTable` machinery:
  1. `runto :L` targets must exist as a label somewhere in the program — either in `program.labels` or in any `function.labels`. Both are valid.
  2. `goto` / `gosub` inside a test must resolve to a label declared inside that same test (or an upstream test in its `from`-chain). Falling through to program labels is an error.
  3. Identifiers referenced inside a test must resolve against (test-locals + mocks + test-functions + `scope_at(most-recent-runto)`). Before any `runto`, the runto component is empty — only globals declared via `global` are present. Function calls into the program reuse the existing function-callsite analysis with the test's current `visible` set as the callsite snapshot.
- **Runtime: two opcodes, one stack, one constructor parameter.** No context-switching machinery, no second instruction pointer, no breakpoint table. Address-as-data plus the VM's existing jump primitives — defer-flavored, not debugger-flavored.
  - **`OpCodes.RUNTO`** (test-side, emitted once per `runto` statement). Pushes `(target_addr, resume_ip)` onto a new `runtoStack` on `VirtualMachine`, then sets `instructionIndex` to where the program is currently paused: the program's `__main` entry on first runto, the saved program IP on subsequent ones.
  - **`OpCodes.RUNTO_YIELD`** (program-side, emitted at every label referenced by a `runto` somewhere in the test corpus). When `runtoStack.Peek().target_addr == instructionIndex`, pops the entry, stores the program's current IP into it, and sets `instructionIndex = resume_ip`. Otherwise falls through. In `dotnet run` builds, `RUNTO_YIELD` is omitted entirely — zero production cost. Labels that aren't runto targets carry no overhead in test builds either.
  - **`runtoStack`** — a new stack on the VM, same family as `scope.deferredJumps`. Just addresses-on-a-stack.
  - **`entryPointAddress` constructor parameter.** Each test's compiled body is a contiguous block in the program blob; its start address lives in the manifest. Running test `foo`: `new VirtualMachine(program, manifest.tests[foo].entryPointAddress)`. Default of `4` is preserved for `dotnet run`.
- **Unified address space.** Test and program bytecode coexist in one `byte[] program` blob. The VM doesn't distinguish contexts; it just executes addresses. `methodStack`, `scopeStack`, `heap` all work as-is — test functions call test functions, program functions call program functions, and the cross-boundary case is handled exclusively by `RUNTO` / `RUNTO_YIELD`. Read/write of program variables by the test goes directly to shared memory; a variable "exists" iff program execution has declared it, type-checked statically by `scope_at(L)` and enforced trivially by memory layout.
- **DAP/debugger flow is preserved.** Because tests run in a single VM with a single `instructionIndex`, attaching the existing debugger works as it does today. Breakpoints in test code and program code both fire normally — they're addresses in the same unified bytecode blob, and the debugger just observes the IP. No multi-target debug session, no new protocol work. The shared-VM model collapses what looked like a multi-process problem into a no-op.
- **Mock dispatch table.** The one place test runs mutate VM-adjacent state, and it's bounded — a single table the command dispatcher consults (per Section 3). Lives at the host boundary, not in the VM core.
- **Manifest emission.** Each `TestNode` emits one entry into a generated `__test_manifest`, including its bytecode `entryPointAddress`, name, optional `from` parent, and source location. Codegen for the test body is mostly the same as a function body, with `runto` lowering to the `RUNTO` opcode described above.

### 9. C# host state resets via `[FadeTestReset]`

Per-test VM isolation handles Fade-side state (variables, heap, stacks). It does *not* handle C# host-side state. Real Fade command implementations frequently carry state — static texture caches, connection pools, allocated GPU resources, logging buffers, singleton subsystems. That state survives the VM's death and leaks into the next test.

For tests against trivial programs (no stateful commands), a fresh VM per test is enough. For real projects, it isn't.

**The `[FadeTestReset]` attribute.** Command authors mark a static method that clears their state. The test runner auto-invokes every method tagged with this attribute before each test runs.

```csharp
public partial class FadeCommands {
    public static int x = 0;

    [FadeCommand("get and up")]
    public static int GetAndUp() { return x++; }

    [FadeCommand("reset get and up")]   // optional — makes it Fade-callable too
    [FadeTestReset]                      // auto-invoked before every test
    public static void ResetGetAndUp() { x = 0; }
}
```

The attribute pattern matches what command authors already know — `[FadeCommand]` is a tag on a static method; `[FadeTestReset]` is another tag on a static method. No new interface, no `IFadeResettable` to inherit from, no `OnTestStart` / `OnTestEnd` lifecycle protocol. Just a method with an attribute.

**Optional dual role.** Tagging the same method with both `[FadeCommand("reset X")]` and `[FadeTestReset]` makes it Fade-callable *and* auto-invoked. The test author can call it explicitly from a Fade test for fine-grained control; the system auto-invokes it for everyone else. Defaults are automatic; opt-out is one line.

**Invocation rules.**
- All `[FadeTestReset]` methods are invoked before each top-level test execution, in registration order.
- For a `from`-chain (e.g., `test child from root`), resets fire once at the start of the chain, then `root`'s body runs, then `child`'s body runs. Resets do not re-fire between parent and child within one execution — the chain is one logical scenario.
- If a reset throws, the test fails fast with `"reset for X failed: ..."` rather than running with stale state.

**Detection.** At command-registration time, walk each `[FadeCommand]`-bearing class for mutable static fields. If a class has any AND no method on it carries `[FadeTestReset]`, emit a build warning:

> *Command class `FadeCommands` has mutable static fields but no `[FadeTestReset]` method — tests using these commands may interfere with each other.*

Detection isn't perfect (a `static Dictionary<int, GpuHandle>` looks "mutable" but the relevant state lives in the handles, not the dictionary; only the author knows which it is), but "any non-readonly static field" catches the overwhelmingly common case. Strict projects can promote the warning to an error via a project setting.

**Sequential.** Tests run one at a time. Resets fire deterministically before each test; only one test touches host state at a time. Parallel execution is out of scope.

## Open questions

What's resolved:

- **Runtime / coroutine implementation.** Two new opcodes (`RUNTO`, `RUNTO_YIELD`) plus one `runtoStack` plus an `entryPointAddress` constructor parameter. No second IP, no breakpoint table, no second VM. See Section 8.
- **Debugger model.** Single VM means the existing DAP flow continues to work without modification. No multi-target debug session needed.
- **Pre-runto function calls.** Reuses the existing function-callsite analysis. The test's `visible` set is just another callsite snapshot — error surfaces at the same place a regular Fade program would error.
- **Cross-file label resolution.** Non-issue. Files concat to one stream at lex time; labels resolve in the unified stream.
- **Parameterized test syntax.** Resolved by tests-as-tokenize-flavored-regions composing with `#macro` for-loops. No special parameterized-test grammar.
- **`runto` across stack frames.** Stack-agnostic. The VM resumes execution as-is from wherever the program was paused; the call stack is honored; `RUNTO_YIELD` fires whenever the IP reaches the target regardless of stack depth. `max cycles` guards against runaway cases.
- **Runto targets.** Any label in the program — top-level or function-internal. Tests can step into a function and assert about its state. `scope_at(:L)` adapts: top-level labels see globals + main-body declarations; function-internal labels see the function's locals plus globals visible at callsites. Both reuse the existing scope checker's per-line scope answer.
- **Mock model (v1).** FIFO queue of behavior entries; `returns <expr>` and `forbid`; frequency words `once`, `n times`, `always` (default `always`); multi-block `mock` declarations append; `clear mock` / `clear mocks` reset; exhausted queue falls through to the real C# implementation; all overloads share a queue. See Section 3.
- **C# host state reset.** `[FadeTestReset]` attribute on a static method, auto-invoked before each test; optionally dual-tagged with `[FadeCommand]` to be Fade-callable too. Build-time warning when a `[FadeCommand]`-bearing class has mutable static fields with no `[FadeTestReset]` method. Sequential execution; parallelism out of scope. See Section 9.

Still open:

1. **Mock extensions beyond v1.** Section 3 lands the v1 surface: FIFO queue, `returns`/`forbid`, frequency words (`once`, `n times`, `always`), `clear mock`/`clear mocks`, exhausted-queue fall-through, all-overloads share a queue. Deferred to a later phase: argument matching (`mock screen width when w > 100`), per-overload disambiguation (`mock screen width(int)`), `body` blocks (Fade code computing the return), `passthrough` keyword (explicit fall-through entry), spy-style call recording for assertions. None of these block v1; they layer on cleanly when needed.
2. **Test discovery for IDE Test Explorer.** Manifest needs source locations so VS / Rider gutter buttons land correctly. For parameterized tests generated via `#macro` loops, locations should point at the originating `test` line, not the expanded output. Probably reuses Fade's existing macro source-mapping.
3. **Failure source-mapping for macro-generated tests.** When `assert` fails inside a macro-generated test, the failure must point at the originating `test` line (and ideally the iteration values via `[name]` substitution preserved in the message), not at the unfathomable expanded location.
4. **`from`-chain implementation.** Section 2 documents replay vs snapshot as observable-equivalent strategies. Replay is the chosen default. Snapshot is deferred until a perf measurement says it matters — but the contract should already be defined in terms of observable behavior so either implementation remains valid.
5. **`endrunto` block clauses beyond `max cycles`.** The block form is extensible by design, but no other clauses are spec'd. Candidates as needs emerge: `unless <condition>`, `while <condition>`, `record events to <list>`, `forbid command X`. Keep deferred until a real test feels the gap.
6. **Runto failure error messages.** When `max cycles` is exceeded, the failure message should capture the program's call stack at the time of failure — *"`runto :sync` exhausted budget; program was inside `update_position()` at the time"* — so the user can see *why* the runto didn't complete. Small piece of runtime plumbing.

Pending decisions, lower priority:

- Whether the `assert` macro should support custom assertion words (`assert close`, `assert in_range`, `assert call_order`) at v1 or evolve them later.

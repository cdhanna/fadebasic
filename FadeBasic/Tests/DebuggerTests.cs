using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Text;
using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Json;
using FadeBasic.Virtual;
using FadeBasic.Launch;

namespace Tests;

public class DebuggerTests
{

    public void Compile(string src, out ProgramNode program, out Compiler compiler, out VirtualMachine vm)
    {
        var lexer = new Lexer();
        var lexerResults = lexer.TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        var parser = new Parser(lexerResults.stream, TestCommands.CommandsForTesting);
        program = parser.ParseProgram();
        program.AssertNoParseErrors();

        compiler = new Compiler(TestCommands.CommandsForTesting, new CompilerOptions
        {
            GenerateDebugData = true
        });
        compiler.Compile(program);

        vm = new VirtualMachine(compiler.Program);
        vm.hostMethods = compiler.methodTable;
    }
    
    [Test]
    public void Exploration_Variables()
    {
        var src = @"
a = 3
b# = 2.3
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        vm.Execute2();

        var variables = DebugUtil.LookupVariables(vm, dbg);

        Assert.That(variables.Count, Is.EqualTo(2));
    }
    
    [Test]
    public void Exploration_Variables_Arrays()
    {
        var src = @"
type vec
    x
    y
endtype
dim x(3,5) as vec
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;

        var session = new DebugSession(vm, dbg, null, new LaunchOptions
        {
            debugWaitForConnection = false,
            debug = true,
            debugPort = 9999
        });
        session.StartDebugging();

        session.variableDb.GetGlobalVariablesForFrame(0);
        var scope = session.variableDb.Expand(2);
        // session.variableDb.Expand()

        var variables = DebugUtil.LookupVariables(vm, dbg, global: true);
        
        
        Assert.That(variables.Count, Is.EqualTo(1));
    }

    [Test]
    public async Task SetValue_ArrayElement_MatchesPlaygroundCallSequence()
    {
        // Replicate the EXACT sequence the Playground's monogame host
        // runs when the user pauses at a bp, expands `n`, and clicks
        // n[1] to set it. If this passes but the user's browser still
        // throws, the running WASM is stale.
        //
        // Sequence (mirrors main.ts → Index.Debug.cs):
        //   1. BREAKPOINT case fires → fetchPausedFramesAndBroadcast
        //      calls DebugScopes(0) (the new top-frame snapshot)
        //   2. refreshDebugView calls DebugScopes(0) again (cached)
        //   3. refreshDebugView walks watches (no-op for now)
        //   4. User clicks "expand n" → DebugVariableExpansion(n.id)
        //   5. User clicks n[1] value, types "3", hits Enter →
        //      DebugSetVariable(0, n[1].id, "3") → Eval → TrySetValue
        var src = @"
glitchamount# = 0
x = 3
y = 5
dim n(3) as integer
n(0) = 0
n(1) = 0
n(2) = 0
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        var session = new DebugSession(vm, dbg, TestCommands.CommandsForTesting, new LaunchOptions
        {
            debug = true, debugPort = 9999, debugWaitForConnection = false
        });
        session.StartDebugging();
        await Task.Delay(50);

        // [1] First DebugScopes — populates idToVariable.
        var scopes1 = session.GetScopes(new DebugScopeRequest { frameIndex = 0 });
        Assert.That(scopes1?.scopes, Is.Not.Null);
        var arrayVar = scopes1.scopes.SelectMany(s => s.variables).FirstOrDefault(v => v.name == "n");
        Assert.That(arrayVar, Is.Not.Null, "should find 'n'");

        // [2] Second DebugScopes — should be cached, return same ids.
        var scopes2 = session.GetScopes(new DebugScopeRequest { frameIndex = 0 });
        var arrayVar2 = scopes2.scopes.SelectMany(s => s.variables).FirstOrDefault(v => v.name == "n");
        Assert.That(arrayVar2?.id, Is.EqualTo(arrayVar.id), "ID must be stable across cached GetScopes calls");

        // [4] Expand n — registers element ids in idToVariable.
        var arrayScope = session.variableDb.Expand(arrayVar.id);
        Assert.That(arrayScope.variables.Count, Is.EqualTo(3));
        var nMid = arrayScope.variables[1]; // n[1] — the element user clicked
        Assert.That(nMid.name, Is.EqualTo("1"));
        Assert.That(nMid.type, Is.EqualTo("Integer"));

        // [5] Set n[1] to 3 via the same call shape DebugSetVariable
        // makes (Eval with overwriteVariableId pointing at the element).
        var result = session.Eval(0, "3", nMid.id);
        Assert.That(result, Is.Not.Null, "Eval returned null — something blew up");
        Assert.That(result.id, Is.Not.EqualTo(-1), $"Eval failed: {result?.value}");

        // ClearLifetime fires after a successful set. Re-fetch and
        // verify the value landed AT n[1] and NOT in the array's
        // register (which would corrupt all elements).
        var scopes3 = session.GetScopes(new DebugScopeRequest { frameIndex = 0 });
        var arrayVar3 = scopes3.scopes.SelectMany(s => s.variables).FirstOrDefault(v => v.name == "n");
        var arrayScope3 = session.variableDb.Expand(arrayVar3.id);
        Assert.That(arrayScope3.variables[0].value, Is.EqualTo("0"), "n[0] must still be 0");
        Assert.That(arrayScope3.variables[1].value, Is.EqualTo("3"), "n[1] should now be 3");
        Assert.That(arrayScope3.variables[2].value, Is.EqualTo("0"), "n[2] must still be 0");
    }

    [Test]
    public async Task SetValue_TopLevelArray_DoesNotThrow_NoVariableForId()
    {
        // The Variables panel renders the array variable's value as
        // "(3)". If the user clicks that cell and types a value, the
        // playground sends DebugSetVariable(frameId, arrayVar.id, rhs).
        // C# may legitimately respond with "types do not match" — but
        // it must NOT throw "no variable for given id" because the
        // array IS in idToVariable (registered by GetVariablesForFrame).
        var src = @"
dim n(3) as integer
n(0) = 1
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        var session = new DebugSession(vm, dbg, TestCommands.CommandsForTesting, new LaunchOptions
        {
            debug = true, debugPort = 9999, debugWaitForConnection = false
        });
        session.StartDebugging();
        await Task.Delay(50);

        var scopes = session.GetScopes(new DebugScopeRequest { frameIndex = 0 });
        var arrayVar = scopes.scopes.SelectMany(s => s.variables).FirstOrDefault(v => v.name == "n");
        Assert.That(arrayVar, Is.Not.Null, "should find 'n'");

        Assert.DoesNotThrow(() =>
        {
            // Same call shape the playground's edit-cell uses for the
            // top-level array variable. C# may return a failure result
            // (id == -1) — that's fine; it's the THROW we're checking
            // doesn't happen.
            var _ = session.Eval(0, "5", arrayVar.id);
        });
    }

    [Test]
    public async Task SetValue_ArrayElement_WritesToHeap()
    {
        // Editing a terminal scalar array element from the Variables
        // panel (`DIM n(3)` → click n[2] → type 5 → Enter) used to throw
        // "no variable for given id" because the element's id from
        // Expand was never registered. Even if we naively registered
        // it in idToTopLevelVariable, TrySetValue's isTop=true branch
        // would write to the ARRAY's register and corrupt the array.
        // The fix registers the element in idToVariable with a
        // runtimeVariable pointing at the element's heap pointer, so
        // TrySetValue's heap-write branch fires.
        var src = @"
dim n(3) as integer
n(0) = 7
n(1) = 8
n(2) = 9
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        var session = new DebugSession(vm, dbg, TestCommands.CommandsForTesting, new LaunchOptions
        {
            debug = true, debugPort = 9999, debugWaitForConnection = false
        });
        session.StartDebugging();
        await Task.Delay(50);

        var scopes = session.GetScopes(new DebugScopeRequest { frameIndex = 0 });
        var allVars = scopes.scopes.SelectMany(s => s.variables).ToList();
        var arrayVar = allVars.FirstOrDefault(v => v.name == "n");
        Assert.That(arrayVar, Is.Not.Null, "should find 'n' array variable");

        var arrayScope = session.variableDb.Expand(arrayVar.id);
        Assert.That(arrayScope.variables.Count, Is.EqualTo(3), "DIM n(3) → 3 elements");
        Assert.That(arrayScope.variables[2].value, Is.EqualTo("9"), "n[2] starts at 9");

        // Set the THIRD element to 42 via the same path the Playground
        // takes — DebugSession.Eval with overwriteVariableId pointing
        // at the element id.
        var thirdId = arrayScope.variables[2].id;
        var result = session.Eval(0, "42", thirdId);
        Assert.That(result, Is.Not.Null);
        Assert.That(result.id, Is.Not.EqualTo(-1), $"set failed: {result?.value}");

        // ClearLifetime ran after the set; re-expand to confirm the
        // VALUE landed in the right slot. n[0] and n[1] must still be
        // their originals; only n[2] should be 42.
        scopes = session.GetScopes(new DebugScopeRequest { frameIndex = 0 });
        arrayVar = scopes.scopes.SelectMany(s => s.variables).First(v => v.name == "n");
        arrayScope = session.variableDb.Expand(arrayVar.id);
        Assert.That(arrayScope.variables[0].value, Is.EqualTo("7"),
            "n[0] must stay 7 — register-write branch would have clobbered the whole array");
        Assert.That(arrayScope.variables[1].value, Is.EqualTo("8"), "n[1] must stay 8");
        Assert.That(arrayScope.variables[2].value, Is.EqualTo("42"), "n[2] should now be 42");
    }

    [Test]
    public async Task Exploration_StructWithFloatField_Expand()
    {
        var src = @"
type measurement
    value#
endtype
m as measurement
m.value# = 3.14
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        var session = new DebugSession(vm, dbg, TestCommands.CommandsForTesting, new LaunchOptions
        {
            debug = true, debugPort = 9999, debugWaitForConnection = false
        });
        session.StartDebugging();
        await Task.Delay(50);

        var scopes = session.GetScopes(new DebugScopeRequest { frameIndex = 0 });
        var allVars = scopes.scopes.SelectMany(s => s.variables).ToList();
        var mVar = allVars.FirstOrDefault(v => v.name == "m");
        Assert.That(mVar, Is.Not.Null, $"Should find 'm' variable");
        Assert.That(mVar.fieldCount, Is.GreaterThan(0), "m should be a struct");

        var structScope = session.variableDb.Expand(mVar.id);
        Assert.That(structScope.variables.Count, Is.EqualTo(1), "struct should have 1 field");

        var valueField = structScope.variables[0];
        Assert.That(valueField.name, Does.Contain("value"), $"field name should contain 'value', got '{valueField.name}'");
        Assert.That(valueField.value, Is.EqualTo("3.14"), $"value should be 3.14, got '{valueField.value}'");
    }

    [Test]
    public async Task Exploration_ArrayOfStruct_Expand()
    {
        var src = @"
type person
    age
    name$
endtype
dim people(3) as person
p as person
p.age = 25
p.name$ = ""hello""
people(0) = p
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        var session = new DebugSession(vm, dbg, TestCommands.CommandsForTesting, new LaunchOptions
        {
            debug = true, debugPort = 9999, debugWaitForConnection = false
        });
        session.StartDebugging();
        await Task.Delay(50);

        // Get all variables from all scopes
        var scopes = session.GetScopes(new DebugScopeRequest { frameIndex = 0 });
        var allVars = scopes.scopes.SelectMany(s => s.variables).ToList();

        // Find the 'people' array variable
        var peopleVar = allVars.FirstOrDefault(v => v.name == "people");
        Assert.That(peopleVar, Is.Not.Null, "Should find 'people' variable");
        Assert.That(peopleVar.elementCount, Is.GreaterThan(0), "people should be an array");

        // Expand the array to get elements
        var arrayScope = session.variableDb.Expand(peopleVar.id);
        Assert.That(arrayScope.variables.Count, Is.GreaterThan(0), "array should have elements");

        // The first element should be a struct with fields
        var elem0 = arrayScope.variables[0];
        Assert.That(elem0.fieldCount, Is.GreaterThan(0), "element 0 should have fields (struct)");

        // Expand element 0 to get struct fields
        var structScope = session.variableDb.Expand(elem0.id);
        Assert.That(structScope.variables.Count, Is.EqualTo(2), "struct should have 2 fields");

        // Check field values
        var ageField = structScope.variables.FirstOrDefault(v => v.name == "age" || v.name == "age#");
        var nameField = structScope.variables.FirstOrDefault(v => v.name == "name$" || v.name == "name");
        Assert.That(ageField, Is.Not.Null, "Should find 'age' field");
        Assert.That(nameField, Is.Not.Null, "Should find 'name$' field");
        Assert.That(ageField.value, Is.EqualTo("25"), "age should be 25");
        Assert.That(nameField.value, Is.EqualTo("hello"), "name should be hello");
    }

    [TestCase(@"`bare field from array-of-struct resolves via element 0
type egg
    e
endtype
dim es(3) as egg
es(0).e = 32
", "e", "32")]
    [TestCase(@"`bare field 'y' from array-of-struct (VS Code sends '.y' stripped to 'y')
type vec
    x
    y
endtype
dim vecs(3) as vec
vecs(0).x = 10
vecs(0).y = 20
", "y", "20")]
    [TestCase(@"`dot-prefixed '.y' from array-of-struct
type vec
    x
    y
endtype
dim vecs(3) as vec
vecs(0).x = 10
vecs(0).y = 20
", ".y", "20")]
    [TestCase(@"`bare 'y' with both struct and array-of-struct in scope (ambiguous - picks first)
type vec
    x
    y
endtype
v as vec
v.x = 10
v.y = 20
dim vecs(3) as vec
vecs(0).x = 100
vecs(0).y = 200
", "y", "20")]
    [TestCase(@"`dotted field from array-of-struct
type egg
    e
endtype
dim es(3) as egg
es(1).e = 32
", "es(1).e", "32")]
    [TestCase(@"`dot-prefixed expression stripped to bare field
type egg
    e
endtype
dim es(3) as egg
es(0).e = 32
", ".e", "32")]
    [TestCase(@"`struct with sigil field (no expand)
type person
    age
    name$
endtype
p as person
p.age = 25
p.name$ = ""hello""
", "p.name$", "hello")]
    [TestCase(@"`bare int field on simple struct (simulates VS Code hover sending just 'x')
type vec
    x
    y
endtype
v as vec
v.x = 42
", "x", "42")]
    [TestCase(@"`bare sigil field on simple struct (simulates VS Code hover sending just 'name')
type person
    age
    name$
endtype
p as person
p.age = 25
p.name$ = ""hello""
", "name", "hello")]
    [TestCase(@"`float sigil field on simple struct (with sigil)
type measurement
    value#
endtype
m as measurement
m.value# = 3.14
", "m.value#", "3.14")]
    [TestCase(@"`float sigil field WITHOUT sigil (VS Code sends 'm.value' not 'm.value#')
type measurement
    value#
endtype
m as measurement
m.value# = 3.14
", "m.value", "3.14")]
    [TestCase(@"`bare float sigil field (simulates VS Code hover sending just 'value')
type measurement
    value#
endtype
m as measurement
m.value# = 3.14
", "value", "3.14")]
    [TestCase("x# = 4.2", "x#+1", "5.2")]
    [TestCase("inc x", "x", "1")]
    [TestCase("tuna x$", "x$", "tuna")]
    [TestCase("x = 4", "x+1", "5")]
    [TestCase(@"
x = 1
function decoyFunction()
endfunction
function sampleFunc(x)
endfunction x + 1
", "sampleFunc(2) + x", "4")]
    [TestCase(@"
dim x(4,9)
x(3,8) = 4
", "x(3,8)+1", "5")]
    [TestCase(@"
type vec
    x
    y
endtype
dim vees(3) as vec
v as vec
v.x = 44
vees(1) = v
", "vees(1).x", "44")]
    [TestCase(@"`array of struct with string field (as string syntax)
type person
    age
    name as string
endtype
dim people(3) as person
p as person
p.age = 25
p.name = ""alice""
people(0) = p
", "people(0).name", "alice")]
    [TestCase(@"`array of struct with string field (sigil syntax)
type person2
    age
    name$
endtype
dim people(3) as person2
p as person2
p.age = 25
p.name$ = ""bob""
people(0) = p
", "people(0).name$", "bob")]
    [TestCase(@"`array of struct WITHOUT sigil (VS Code sends 'people(0).name' not 'people(0).name$')
type person2
    age
    name$
endtype
dim people(3) as person2
p as person2
p.age = 25
p.name$ = ""bob""
people(0) = p
", "people(0).name", "bob")]
    [TestCase(@"
type vec
    x
    y
endtype
a = 5
v as vec
v.x = 3
v.y = 2
", "v", "[vec]")]
    [TestCase(@"
type vec
    x
    y
endtype
type egg
    a 
    v as vec
endtype
n = 6
v as vec
v.x = 3
v.y = 2
`e as egg
`e.a = 3
`e.v = v
", "n:v", "6:[vec]")]
    [TestCase(@"`simple array case
dim x(3)
x(1) = 4
", "x", "(3)")]
    [TestCase(@"`struct array case
type vec
    x
    y
endtype
dim x(3) as vec
x(1).x = 4
", "x", "(3)")]
    public async Task Exploration_Eval(string src, string evalGroup, string expectedGroup)
    {
        var evals = evalGroup.Split(":", StringSplitOptions.RemoveEmptyEntries);
        var expects = expectedGroup.Split(":", StringSplitOptions.RemoveEmptyEntries);
        if (evals.Length != expects.Length) throw new InvalidOperationException("bad test input");

        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        var session = new DebugSession(vm, dbg, TestCommands.CommandsForTesting, new LaunchOptions
        {
            debug = true, debugPort = 9999, debugWaitForConnection = false
        });
        session.StartDebugging();
        await Task.Delay(50); // give some time for the program to finish executing... 

        for (var i = 0; i < evals.Length; i++)
        {
            var eval = evals[i];
            var expected = expects[i];
            var res = session.Eval(0, eval);
            Assert.That(res.id, Is.GreaterThanOrEqualTo(0), $"Eval('{eval}') returned failed result: {res.value}");
            Assert.That(res.value, Is.EqualTo(expected));

        }

    }
    
    [TestCase(@"`basic case
x = 5
", "x", "8", "8", 3, new int[]{})]
    [TestCase(@"`basic float
x# = 5.2
", "x#", "8.3", "8.3", 3, new int[]{})]
    
    [TestCase(@"`basic float (but looks like int)
x# = 5.2
", "x#", "8", "8", 3, new int[]{})]
    
    [TestCase(@"`basic byte
x as byte = 5
", "x", "8", "8", 3, new int[]{})]

    [TestCase(@"`basic double integer
x as double integer = 5
", "x", "8", "8", 3, new int[]{})]

    [TestCase(@"`accessor
type vec
    x
    y
endtype
v as vec
v.x = 5
", "v.x", "8", "8", 5, new int[]{3})]
    [TestCase(@"`replace struct
type vec
    x
    y
endtype
v as vec
v2 as vec
v.x = 5
v2.x = 10
", "v", "v2", "[vec]", 3, new int[]{})]
    public async Task Exploration_Expr(string src, string lhs, string rhs, string expected, int variableId, int[] idsToExpand)
    {
        
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        var session = new DebugSession(vm, dbg, TestCommands.CommandsForTesting, new LaunchOptions
        {
            debug = true, debugPort = 9999, debugWaitForConnection = false
        });
        session.StartDebugging();
        await Task.Delay(50); // give some time for the program to finish executing... 
        
        session.GetScopes(new DebugScopeRequest
        {
            frameIndex = 0
        });
        foreach (var id in idsToExpand)
        {
            session.variableDb.Expand(id);
        }
        
        var res = session.Eval(0, rhs, variableId);
        

        
        Assert.That(res.value, Is.EqualTo(expected));
    }


    [Test]
    public void Exploration_Variables_Structs()
    {
        var src = @"
TYPE egg
    x
ENDTYPE

greg AS egg
greg.x = 3
dan AS egg
dan.x = 5

";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        vm.Execute2();

        var variables = DebugUtil.LookupVariables(vm, dbg, global: false);

        Assert.That(variables.Count, Is.EqualTo(2));
    }

    
    [Test]
    public void FunctionMap()
    {
        var src = @"n = 1
igloo(n)

function igloo(y)
x = y * 2
toast()
endfunction

function toast()
endfunction
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;
        var map = new IndexCollection(dbg.statementTokens);
     
    }

    [Test]
    public void IndexMap()
    {
        var src = @"n = 0
igloo()

function igloo()
    print ""toast""
    while 1 > 0
        inc n
        print ""hello"", n
        wait ms 500
        getVm
    endwhile
endfunction";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;

        var map = new IndexCollection(dbg.statementTokens);
        if (!map.TryFindClosestTokenBeforeIndex(176, out var lastToken))
        {
            Assert.Fail("There should be something for the last token");
        }
        // var tree = IntervalTree.From(dbg.points);
// ITS ONLY GETTING LINE 1 for some reason?


// TODO: 182 is not even in the tree, but that is the number that is being hit in real life after step-over. 
        // var hasIndex = tree.TryFind(182, out var index);
    }

    [Test]
    public void StepOver_SwitchExit_HasComputedToken_NotLastCase()
    {
        // Regression: stepping over a matched CASE body used to pause on the
        // LAST CASE line (which never executed). Every case body jumps to the
        // switch's exit NOOP, but that NOOP had no debug token, so
        // TryFindClosestTokenBeforeIndex resolved it to the last case body's
        // real token — a stop point for step-over. The exit must carry a
        // COMPUTED token so the stepper skips it (mirrors Compile(IfStatement)).
        var src = @"x = 1
SELECT (x)
    CASE 0
        PRINT ""zero""
    ENDCASE
    CASE 1
        PRINT ""one""
    ENDCASE
    CASE 2
        PRINT ""two""
    ENDCASE
ENDSELECT
PRINT ""done""
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;

        var real = dbg.statementTokens.Where(t => t.isComputed == 0).ToList();
        // PRINT "two" (last case body) is on line 9 (0-based); PRINT "done" line 12.
        var lastCaseTok = real.First(t => t.token.lineNumber == 9);
        var doneTok = real.First(t => t.token.lineNumber == 12);

        var exitComputed = dbg.statementTokens
            .Where(t => t.isComputed == 1 && t.insIndex > lastCaseTok.insIndex && t.insIndex < doneTok.insIndex)
            .ToList();
        Assert.That(exitComputed, Is.Not.Empty,
            "the switch exit must carry a computed token between the last case body and the next statement");

        // And the exit instruction resolves to that computed token, not the
        // last case's real token — so step-over won't pause on the last case.
        var map = new IndexCollection(dbg.statementTokens);
        Assert.That(map.TryFindClosestTokenBeforeIndex(exitComputed[0].insIndex, out var resolved), Is.True);
        Assert.That(resolved.isComputed, Is.EqualTo(1),
            "the switch exit index must resolve to a computed (skip) token");
    }

    [Test]
    public void ExpandVariable_MultiDimArray_ExpandsEverySibling()
    {
        // Regression: expanding a multi-dimensional array only worked for the
        // FIRST sub-array. Element pointers past index 0 were computed with raw
        // `rawValue + offset` instead of VmPtr arithmetic, corrupting the heap
        // pointer so ReadSpan threw and the sibling expansion came back empty.
        var src = "DIM numbers(3, 5)\n";
        Compile(src, out _, out var compiler, out var vm);
        vm.Execute2();

        var db = new DebugVariableDatabase(vm, compiler.DebugData, new EmptyDebugLogger());
        var numbers = db.GetGlobalVariablesForFrame(0).variables.First(v => v.name == "numbers");

        var rows = db.Expand(numbers.id);
        Assert.That(rows.variables.Count, Is.EqualTo(3), "numbers(3,5) has 3 rows");
        foreach (var row in rows.variables)
        {
            var cols = db.Expand(row.id);
            Assert.That(cols.variables.Count, Is.EqualTo(5),
                $"row '{row.name}' should expand to 5 elements (not just the first sibling)");
        }
    }

    [Test]
    public async Task DebugServerDemo()
    {
        var port = LaunchUtil.FreeTcpPort();
        var src = @$"
b = 1
b2 = 2
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;

        var session = new DebugSession(vm, dbg, null, new LaunchOptions
        {
            debug = true,
            debugPort = port,
            debugWaitForConnection = true
        });
        session.StartServer();
        
        session.StartDebugging(2);
        Assert.That(session.InstructionPointer, Is.EqualTo(4),
            "because the client has not connected yet, the program should not have run at all, even through there was budget");
        
        var remote = new RemoteDebugSession(port);
        remote.Connect();
        remote.SayHello();

        await Task.Delay(100); // fluff time for the connection to happen...
        
        session.StartDebugging(1);
        Assert.That(session.InstructionPointer, Is.EqualTo(4),
            "Exactly 1 op will let the debugger attach, but the program counter has no budget left");
        
        session.StartDebugging(3);
        Assert.That(session.InstructionPointer, Is.EqualTo(12),
            "I happen to know that 3 is a magical number of budget to yield 12 as an instruction index...");

        
        var receivedConf = false;

        { // verify that a pause event can be sent
            remote.SendPause(() => { receivedConf = true; });

            await Task.Delay(100); // fluff time for the message to send
            session.StartDebugging(
                2); // read the message (1 op for the read, and 1 op to be ignored because we are paused)
            await Task.Delay(100); // fluff time for the ack to emit

            Assert.That(receivedConf, Is.True);
            Assert.That(session.InstructionPointer, Is.EqualTo(12),
                "The debugger should be paused, so the insptr should not have moved from last time.");
        }
        
        { // check stack frames
            remote.RequestStackFrames(frames =>
            {
                
            });
            await Task.Delay(100); // fluff time for the message to send
            
            session.StartDebugging(1); // read the message, but do not process
            await Task.Delay(100); // fluff time for the ack to emit
        }

        { // verify that a play event can be sent
            receivedConf = false;
            remote.SendPlay(() => { receivedConf = true; });

            await Task.Delay(100); // fluff time for the message to send
            session.StartDebugging(3); // read the message (1 op for the read, and 1 op to move the debugger forward)
            await Task.Delay(100); // fluff time for the ack to emit
            Assert.That(receivedConf, Is.True);
            Assert.That(session.InstructionPointer, Is.EqualTo(27),
                "The debugger should be paused, so the insptr should not have moved from last time.");
        }
        
        { // check variables
            // var opsLeft = session.InstructionPointer - 
            session.StartDebugging(3); // allow the rest of the program to execute
            await Task.Delay(100); // fluff time for program to run
            remote.RequestScopes(0, scopes =>
            {
                
            });
            await Task.Delay(100); // fluff time for the message to send
            
            session.StartDebugging(1); // read the message, but do not process
            await Task.Delay(100); // fluff time for the ack to emit
        }



    }
    
    
    // [Test] // TODO: Need to figure out how to let this run on Github :(
    public async Task DebugServerTest_Big()
    {
        var port = LaunchUtil.FreeTcpPort();
        
        var variableCount = 1000;
        var sb = new StringBuilder();
        for (var i = 0; i < variableCount; i++)
        {
            sb.AppendLine($"a{i} = {i}");
        }

        var src = @$"
b = 1
b2 = 2
{sb.ToString()}
";
        Compile(src, out _, out var compiler, out var vm);
        var dbg = compiler.DebugData;

        var session = new DebugSession(vm, dbg, null, new LaunchOptions
        {
            debug = true,
            debugPort = port,
            debugWaitForConnection = true
        });
        session.StartServer();
        
        session.StartDebugging(2);
        Assert.That(session.InstructionPointer, Is.EqualTo(4),
            "because the client has not connected yet, the program should not have run at all, even through there was budget");
        
        var remote = new RemoteDebugSession(port);
        remote.Connect();
        remote.SayHello();

        await Task.Delay(100); // fluff time for the connection to happen...
        
        session.StartDebugging(1);
        Assert.That(session.InstructionPointer, Is.EqualTo(4),
            "Exactly 1 op will let the debugger attach, but the program counter has no budget left");
        
        session.StartDebugging(2);
        Assert.That(session.InstructionPointer, Is.EqualTo(12),
            "I happen to know that 2 is a magical number of budget to yield 12 as an instruction index...");

        
        var receivedConf = false;

        { // verify that a pause event can be sent
            remote.SendPause(() => { receivedConf = true; });

            await Task.Delay(100); // fluff time for the message to send
            session.StartDebugging(
                2); // read the message (1 op for the read, and 1 op to be ignored because we are paused)
            await Task.Delay(100); // fluff time for the ack to emit

            Assert.That(receivedConf, Is.True);
            Assert.That(session.InstructionPointer, Is.EqualTo(12),
                "The debugger should be paused, so the insptr should not have moved from last time.");
        }
        
        { // check stack frames
            remote.RequestStackFrames(frames =>
            {
                
            });
            await Task.Delay(100); // fluff time for the message to send
            
            session.StartDebugging(1); // read the message, but do not process
            await Task.Delay(100); // fluff time for the ack to emit
        }

        { // verify that a play event can be sent
            receivedConf = false;
            remote.SendPlay(() => { receivedConf = true; });

            await Task.Delay(100); // fluff time for the message to send
            session.StartDebugging(2); // read the message (1 op for the read, and 1 op to move the debugger forward)
            await Task.Delay(100); // fluff time for the ack to emit
            Assert.That(receivedConf, Is.True);
            Assert.That(session.InstructionPointer, Is.EqualTo(27),
                "The debugger should be paused, so the insptr should not have moved from last time.");
        }
        
        { // check variables
            var lines = src.Count(x => x == '\n');
            if (!session.instructionMap.TryFindClosestTokenAtLocation(lines - 1, 0, out var token))
            {
                Assert.Fail("no token found");
            }
            // var opsLeft = session.InstructionPointer - 
            session.StartDebugging(3 + (500 * 5)); // allow the rest of the program to execute
            await Task.Delay(100); // fluff time for program to run
            // Assert.That(session.InstructionPointer, Is.EqualTo(vm.program.Length - 1),
            //     "I happen to know that 3 more steps gets the program to finish at line 44");
            //
            var hit = false;
            remote.RequestScopes(0, scopes =>
            {
                var locals = scopes[1];
                var localCount = locals.variables.Count;
                hit = true;
            });
            await Task.Delay(100); // fluff time for the message to send
            
            session.StartDebugging(1); // read the message, but do not process
            
            await Task.Delay(2500); // fluff time for the ack to emit. Giant for large message
            
            Assert.IsTrue(hit);
        }



    }
    

}
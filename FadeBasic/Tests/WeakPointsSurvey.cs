using FadeBasic;
using FadeBasic.Ast;
using FadeBasic.Lib.Standard;
using Tests.CSharpBackend;

namespace Tests;

/// <summary>
/// Regression tests for "Fade Basic weak points, as met in Fish Fish Pop" (found on 0.2.6.1):
/// each entry as a standalone program, which must give the right answer on the VM and on the
/// C# backend.
/// </summary>
[TestFixture]
public class WeakPointsSurvey
{
    public static readonly CommandCollection Commands = new(new StandardCommands(), new ConsoleCommands());

    public static readonly (string name, string expected, string src)[] Cases =
    {
        ("W1 subtraction groups right", "9", @"
x = 10 - 3 + 2
print x"),
        ("W1b a - b - c", "5", @"
x = 10 - 3 - 2
print x"),
        ("W2 name shared with a struct elsewhere", "3 4", @"
type creditLine
  a
endtype
countThings()
function showCredits()
  entry as creditLine
  entry.a = 1
endfunction
function countThings()
  entry = 3
  print entry
  entry = entry + 1
  print entry
endfunction"),
        ("W3 struct declared in code that never runs", "3", @"
type vec
  x
  y
endtype
gosub myRoutine
end
v as vec
myRoutine:
  v.x = 3
  print v.x
return"),
        ("W4a void call inside a function used in a sum", "105", @"
global total
x = 5 + work()
print x
function noop()
  total = total + 1
endfunction
function work()
  noop()
endfunction 100"),
        ("W4b descending for loop inside a function used in a sum", "255", @"
x = 5 + f()
print x
function f()
  for i = 3 to 1 step -1
  next
endfunction 250"),
        ("W4c several return points in a sum", "355", @"
g = 2
w = 5
w = w + waitFor(g)
print w
function isSpecial(n)
endfunction n > 1
function otherWait(n)
  for i = 5 to 1 step -1 : next
endfunction n * 125
function waitFor(groupId)
  if groupId = 0 then exitfunction 0
  if isSpecial(groupId) then exitfunction otherWait(groupId) + 100
endfunction 250"),
        ("C1 function returning a float", "2.5", @"
x# = halfOf(5)
print x#
function halfOf(n)
  h# = n / 2.0
endfunction h#"),
        ("C2 float compare in assert (main)", "ok", @"
a# = 1.5 : b# = 2.5
assert a# < b#
print ""ok"""),
        ("C3 int variable assigned a float, used as an index", "2", @"
dim names(5)
names(2) = 2
slot = 10 / 4.0
print names(slot)"),
        ("C4 joining a stored chr$", "Cx", @"
c$ = chr$(67)
d$ = c$ + ""x""
print d$"),
        ("P1 one-line if ending in a call", "loop done", @"
a = 1 : b = 1
for i = 0 to 2
  if a and b then doThing(i, 1, 2)
next
print ""loop done""
function doThing(i, x, y)
endfunction"),
        ("P2 comment after #constant", "6", @"
#constant ORDER_TOP 6 `drawn last
x = ORDER_TOP
print x"),
        ("P3 constant replaces a label", "in routine", @"
#constant CORAL_ENDING_REEF 1
gosub coral_ending_reef
end
coral_ending_reef:
  print ""in routine""
return"),
        ("P4 arithmetic left of a comparison", "1", @"
a = 5 : b = 2 : c = 3
x = 0
if a - b >= c then x = 1
print x"),
        ("P5 and after a command call", "yes", @"
y = 1
if val(""1"") and y = 1 then print ""yes"""),
        ("P6 string <>", "differs", @"
w$ = ""a""
if w$ <> """" then print ""differs"""),
        ("P6b len of a string", "3", @"
w$ = ""abc""
print len(w$)"),
        ("P7b variable named wait", "3", @"
wait = 3
print wait"),
        ("T1 mock reads its parameter", "PASS", @"
test t
  mock rnd(tMax)
  endmock tMax - 1
  assert rnd(10) = 9
endtest"),
        ("T2 exitmock inside an if", "PASS", @"
test t
  global tRolls
  mock rnd
    tRolls = tRolls + 1
    if tRolls > 1
      exitmock 7
    endif
  endmock 0
  a = rnd(5)
  b = rnd(5)
  assert b = 7
endtest"),
        ("C5 test local named like a struct local in another test", "PASS", @"
type vec
  x
  y
endtype
test first
  tAt as vec
  tAt.x = 1
endtest
test second
  tAt = 5
  assert tAt = 5
endtest"),
        ("C2b float compare in assert (test)", "PASS", @"
test t
  a# = 1.5 : b# = 2.5
  assert a# < b#
endtest"),
        ("T6 test without end", "PASS", @"
x = 0
_loop:
x = x + 1
if x < 1000 then goto _loop
test t
  runto _loop
  assert x >= 0
endtest"),
        // the examples in the book's Order of Operations, constants and mocks sections
        ("B1 order of operations", "5 9 512 5 5 0", @"
print 10 - 3 - 2
print 10 - 3 + 2
print 2 ^ 3 ^ 2
print 1 + 2 << 1
print 5 or 0
a = 0 : b = 0 : c = 1
print a and b or c"),
        ("B2 constant beside a label", "in the reef", @"
#constant reef 1
gosub reef
end
reef:
  print ""in the reef""
return"),
        ("B3 forbid inside an if, not reached", "PASS", @"
wait ms(100)
_L1:
test sample
  mock wait ms(ms)
    if ms > 500
      forbid ""no long waits""
    endif
  endmock
  runto _L1
endtest"),
        ("B3b forbid inside an if, reached", "forbidden command was called: wait ms | no long waits", @"
wait ms(1000)
_L1:
test sample
  mock wait ms(ms)
    if ms > 500
      forbid ""no long waits""
    endif
  endmock
  runto _L1
endtest"),
        ("T7 mock parameter in a from-test", "PASS", @"
x = 0
_loop:
x = x + 1
if x < 1000 then goto _loop
test parent
  runto _loop
endtest
test child from parent
  mock rnd(tMax)
  endmock tMax - 1
  assert rnd(10) = 9
endtest"),
    };

    static List<CSharpBackendHarness.Case>? _cases;

    static List<CSharpBackendHarness.Case> Prepared()
    {
        if (_cases != null) return _cases;
        var cases = Cases.Select(c => new CSharpBackendHarness.Case { Name = c.name, Source = c.src, Commands = Commands }).ToList();
        foreach (var c in cases) CSharpBackendHarness.Prepare(c);
        CSharpBackendHarness.CompileAll(cases);
        return _cases = cases;
    }

    public static IEnumerable<TestCaseData> Names() => Cases.Select(c => new TestCaseData(c.name).SetName(c.name.Split(' ')[0]));

    [TestCaseSource(nameof(Names))]
    public void BothBackendsGetItRight(string name)
    {
        var c = Prepared().First(x => x.Name == name);
        var expected = Cases.First(x => x.name == name).expected;
        Assert.That(c.SkipReason, Is.Null);
        Assert.That(c.TranspileError, Is.Null);
        Assert.That(c.CompiledType, Is.Not.Null, c.CSharpCompileError);

        var vm = c.Vm!;
        var cs = CSharpBackendHarness.RunCompiled(c.CompiledType!);
        if (vm.Tests.Count > 0)
        {
            foreach (var t in vm.Tests)
            {
                Assert.That(t.Failure ?? t.Error ?? "PASS", Is.EqualTo(expected), "vm test " + t.Name);
                var ct = CSharpBackendHarness.RunCompiledTest(c.CompiledType!, t.Name);
                Assert.That(ct.Failure ?? ct.Error ?? "PASS", Is.EqualTo(expected), "c# test " + t.Name);
            }
            return;
        }
        Assert.That(vm.Error, Is.Null, "vm");
        Assert.That(Flat(vm.Output), Is.EqualTo(expected), "vm");
        Assert.That(cs.Error, Is.Null, "c#");
        Assert.That(Flat(cs.Output), Is.EqualTo(expected), "c#");
    }

    [Test]
    public void ReservedWordAsParameterNameSaysSo()
    {
        var ast = CSharpBackendHarness.Parse(@"print advance(1)
function advance(step)
endfunction step + 1", Commands, out var error);
        Assert.That(ast, Is.Null);
        Assert.That(error, Does.Contain("`step` is a reserved word"));
    }

    [TestCase(@"for i = 0 to 9
  y = 2
")]
    [TestCase(@"if x = 1
  y = 2
")]
    [TestCase(@"while x
  y = 2
")]
    public void AnUnterminatedBlockDoesNotSwallowTheNextType(string broken)
    {
        var src = broken + @"type vec
  x
endtype
";
        for (var i = 0; i < 20; i++) src += $@"v{i} as vec
v{i}.x = {i}
";
        var lex = new Lexer().TokenizeWithErrors(src, Commands);
        var program = new Parser(new TokenStream(lex.tokens), Commands).ParseProgram();
        var errors = program.GetAllErrors();
        Assert.That(errors.Count, Is.LessThanOrEqualTo(2), string.Join("; ", errors.Select(e => e.Display)));
    }

    static string Flat(string s) => string.Join(" ", s.Split('\n', StringSplitOptions.RemoveEmptyEntries));
}

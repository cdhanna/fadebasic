namespace Tests.CSharpBackend;

/// <summary>
/// Differential tests for the C# backend: each program runs on the VM and as compiled C#, and
/// the two must agree on output, errors and final global registers.
/// </summary>
[TestFixture]
public class CSharpBackendTests
{
    /// <summary>Hand-written programs aimed at specific VM semantics.</summary>
    public static readonly (string name, string src)[] Semantics =
    {
        ("int_math", @"
a = 7 : b = 2
c = a / b : d = a mod b : e = a * b - 3 : f = a ^ b
print c, d, e, f"),
        ("float_promotion", @"
x# = 7 / 2
y# = 7.0 / 2
z = 7.5 + 1
w# = 1 + 0.25
print x#, y#, z, w#"),
        ("tie_goes_to_left", @"
a as integer : b as double integer
a = 5 : b = 2000000000 : b = b * 2
c as double integer
c = b + a
d = a + b
print c, d"),
        ("byte_subtract_is_int", @"
a as byte : b as byte
a = 3 : b = 5
c = a - b
print c"),
        ("truthiness", @"
if -1 then print ""neg true"" else print ""neg false""
if 0.5 then print ""half true"" else print ""half false""
if 2 then print ""two true""
x = 0
while x < 3 : x = x + 1 : endwhile
print x"),
        ("or_keeps_left_value", @"
a = 5 or 0
b = 0 or 7
c = 3 and 0
d = 0 and 9
e = -2 and 4
print a, b, c, d, e"),
        ("for_loops", @"
for i = 1 to 3 : print i : next
for i = 3 to 1 : print i : next
for i = 0 to 10 step 4 : print i : next
for x# = 0 to 1 step 0.25 : print x# : next
t = 0
for i = 1 to 5
  if i = 2 then skip
  if i = 4 then exit
  t = t + i
next
print t, i"),
        ("for_start_reevaluated", @"
global calls
calls = 0
for i = start() to 3
next
print calls
function start()
  calls = calls + 1
endfunction 1"),
        ("repeat_do", @"
n = 0
repeat
  n = n + 1
until n >= 4
do
  n = n + 10
  if n > 40 then exit
loop
print n"),
        ("strings", @"
a$ = ""hello""
b$ = a$ + "" "" + ""world""
print b$, len(b$)
if a$ = ""hello"" then print ""eq""
if ""b"" > ""a"" then print ""gt""
if ""z"" > ""a"" then print ""z gt"" else print ""z not gt (VM quirk)""
if ""a"" < ""b"" then print ""lt"""),
        ("select_case", @"
for i = 0 to 4
  select i
    case 1 : print ""one"" : endcase
    case 2, 3 : print ""two-three"" : endcase
    case default : print ""other"" : endcase
  endselect
next"),
        ("select_string_identity", @"
a$ = ""bob""
b$ = ""b"" + ""ob""
select a$
  case ""bob"" : print ""a matched"" : endcase
  case default : print ""a no match"" : endcase
endselect
select b$
  case ""bob"" : print ""b matched"" : endcase
  case default : print ""b no match"" : endcase
endselect"),
        ("gosub_goto", @"
x = 0
gosub addOne
gosub addOne
goto skipIt
x = 100
skipIt:
print x
end
addOne:
  x = x + 1
return"),
        ("functions", @"
print plus(2, 3), half(7), greet(""fade""), greet("""")
function plus(a, b)
endfunction a + b
function half(n#)
endfunction n# / 2
function greet(name$)
  if name$ = """" then exitfunction ""nobody""
endfunction ""hi "" + name$"),
        ("recursion", @"
print fib(15)
function fib(n)
  if n < 2 then exitfunction n
endfunction fib(n - 1) + fib(n - 2)"),
        ("function_locals_are_fresh", @"
x = 5
print readX(), x
function readX()
  local x as integer
  x = x + 1
endfunction x"),
        ("globals_in_functions", @"
global total
total = 1
bump() : bump()
print total
function bump()
  total = total * 3
endfunction"),
        ("types", @"
type vec
  x as float
  y as float
endtype
type actor
  name$
  pos as vec
  hp
endtype
a as actor
a.name$ = ""fish""
a.pos.x = 1.5
a.hp = 10
b as actor
b = a
b.hp = 3
print a.hp, b.hp, b.name$, b.pos.x
print len(b.name$)"),
        ("struct_params_copy", @"
type pt
  x
  y
endtype
p as pt
p.x = 1
q as pt
q = moved(p)
print p.x, q.x
function moved(v as pt)
  v.x = v.x + 10
endfunction v"),
        ("arrays", @"
dim a(5)
for i = 0 to 4 : a(i) = i * i : next
dim grid(3, 4)
grid(2, 3) = 7
print a(3), grid(2, 3), len(a), len(grid, 1)
redim a(2)
print a(0), len(a)"),
        ("array_of_types", @"
type cell
  kind
  score#
endtype
dim cells(4) as cell
cells(1).kind = 3
cells(1).score# = 2.5
c as cell
c = cells(1)
print c.kind, c.score#"),
        ("array_assign_copies", @"
dim a(3)
dim b(3)
a(1) = 5
b = a
a(1) = 9
print a(1), b(1)"),
        ("bounds_error", @"
dim a(3)
a(1) = 2
print a(1)
a(3) = 1
print ""unreachable"""),
        ("divide_by_zero_float", @"
x# = 0
y# = 1 / x#
print ""unreachable"""),
        ("defer", @"
test1()
print ""after""
function test1()
  defer print ""deferred 1""
  defer
    print ""deferred 2""
  enddefer
  print ""body""
endfunction"),
        ("ref_commands", @"
x = 3
refDbl x
inc x, 5
inc y
print x, y
dim a(2)
a(1) = 4
inc a(1)
print a(1)"),
        ("params_commands", @"
print sum(1, 2, 3), sum()
dim nums(3)
nums(0) = 4 : nums(1) = 5 : nums(2) = 6
print sum(nums)"),
        ("command_overloads", @"
print ovrtwice(3), ovrtwice(2.5), ovrret(4), ovrret(""s"")
a = 1 : ovrbump a, 2
b# = 1 : ovrbump b#, 0.5
print a, b#"),
        ("prims", @"
w as word : w = 65535 : w = w + 1
dw as dword : dw = 0 : dw = dw - 1
by as byte : by = 255 : by = by + 1
d as double float : d = 1.5
di as double integer : di = 2
print w, dw, by, d * 2, di * 3
print prim test di(21), prim test w(3), prim test f(1.25), prim test b(4)"),
        ("double_equality_quirk", @"
d as double float
d = 2
if d = 2 then print ""eq"" else print ""never equal (VM quirk)""
if d <> 2 then print ""always unequal (VM quirk)"""),
        ("end_inside_function", @"
print ""start""
stop()
print ""unreachable""
function stop()
  print ""stopping""
  end
endfunction"),
        ("labels_in_functions", @"
print count(3)
function count(n)
  total = 0
  again:
  total = total + n
  n = n - 1
  if n > 0 then goto again
endfunction total"),
        ("sync_suspends", @"
x = 0
for i = 1 to 3
  x = x + 1
  getVm
next
print x"),
        ("suspend_mid_function", @"
print work(4)
function work(n)
  t = 0
  for i = 1 to n
    t = t + i
    cls
  next
endfunction t * helper()
function helper()
  cls 1
endfunction 2"),
        ("xor_not", @"
a = 1 xor 0
b = 1 xor 1
c = not 0
d = not 5
e = not 0.5
print a, b, c, d, e"),
        ("bitwise", @"
a = 12 && 10
b = 12 || 3
c = 1 << 4
d = 256 >> 2
print a, b, c, d"),
        ("routines_nested", @"
total = 0
for i = 1 to 3
  gosub outer
next
print total
end
outer:
  total = total + 1
  gosub inner
  if total > 100 then return
  total = total + 10
return
inner:
  total = total * 2
return"),
        ("routine_that_suspends", @"
frames = 0
for i = 1 to 3
  gosub frame
next
print frames
end
frame:
  frames = frames + 1
  getVm
return"),
        ("routine_entered_by_fallthrough_or_goto", @"
x = 0
gosub a
gosub b
goto b
c:
print x
end
a:
  x = x + 1
b:
  x = x + 10
  if x > 25 then goto c
return"),
        ("routine_recursion_and_loop_back", @"
n = 0
gosub count
print n
end
count:
  n = n + 1
  if n < 5 then gosub count
  if n < 8
    n = n + 1
    goto count
  endif
return"),
        ("runto_inside_a_routine", @"
score = 0
do
  gosub play
  if score > 30 then end
loop
end
play:
  score = score + 7
  _scored:
  score = score + 1
return
test stops_inside_play
  runto _scored
  assert score = 7
  runto _scored
  assert score = 15
endtest
test stops_at_a_routine_label
  runto play
  assert score = 0
  runto play
  assert score = 8
endtest"),
    };

    /// <summary>
    /// Programs where the backends differ on purpose. Each is a VM behaviour that reads
    /// uninitialized memory, so there is nothing meaningful to copy.
    /// </summary>
    static readonly Dictionary<string, string> KnownDivergences = new()
    {
        ["TokenVm#142"] = UnsetString,
        ["TokenVm#143"] = UnsetString,
        ["TokenVm#144"] = UnsetString,
    };

    const string UnsetString =
        "an unassigned string array element is heap pointer 0, which the VM reads as whatever " +
        "allocation happens to live there; the C# backend reads it as \"\"";

    static List<CSharpBackendHarness.Case>? _cases;
    static readonly object Gate = new();

    static List<CSharpBackendHarness.Case> Cases()
    {
        lock (Gate)
        {
            if (_cases != null) return _cases;
            var cases = new List<CSharpBackendHarness.Case>();
            foreach (var (name, src) in Semantics.Concat(CSharpBackendCorpus.Harvested()))
            {
                var c = new CSharpBackendHarness.Case { Name = name, Source = src };
                try
                {
                    CSharpBackendHarness.Prepare(c);
                }
                catch (Exception ex)
                {
                    c.SkipReason ??= "harness: " + ex.Message;
                }
                cases.Add(c);
            }
            CSharpBackendHarness.CompileAll(cases);
            _cases = cases;
            return cases;
        }
    }

    public static IEnumerable<TestCaseData> SemanticCases() =>
        Semantics.Select(s => new TestCaseData(s.name).SetName("Semantics_" + s.name));

    public static IEnumerable<TestCaseData> CorpusCases() =>
        CSharpBackendCorpus.Harvested().Select(s => new TestCaseData(s.name).SetName("Corpus_" + s.name.Replace('#', '_')));

    [TestCaseSource(nameof(SemanticCases))]
    public void Semantic(string name) => Check(name, strict: true);

    [TestCaseSource(nameof(CorpusCases))]
    public void Corpus(string name) => Check(name, strict: false);

    static void Check(string name, bool strict)
    {
        var c = Cases().First(x => x.Name == name);
        if (c.SkipReason != null)
        {
            if (strict) Assert.Fail("skipped: " + c.SkipReason);
            Assert.Ignore(c.SkipReason);
        }
        if (c.TranspileError != null && c.Vm?.Error != null && c.Vm.Error.StartsWith("compile:"))
        {
            Assert.Pass("both backends reject the program: " + c.TranspileError);
        }
        if (c.TranspileError != null)
        {
            if (strict) Assert.Fail("not supported by the C# backend: " + c.TranspileError);
            Assert.Inconclusive("not supported by the C# backend: " + c.TranspileError);
        }
        if (c.CSharpCompileError != null || c.CompiledType == null)
        {
            Assert.Fail($"generated C# does not compile: {c.CSharpCompileError}\n{c.CSharpSource}");
        }

        var run = CSharpBackendHarness.RunCompiled(c.CompiledType!);
        if (CSharpBackendCorpus.IsNondeterministic(c.Source))
        {
            Assert.That(run.Error is null or not "TIMEOUT", "compiled program hung");
            Assert.Pass("nondeterministic program: ran without comparing");
        }

        var diff = CSharpBackendHarness.Compare(c, run);
        if (diff != null && KnownDivergences.TryGetValue(name, out var why))
        {
            Assert.Inconclusive("known VM divergence: " + why + Environment.NewLine + diff);
        }
        if (diff != null)
        {
            Assert.Fail($"{diff}\n\n--- fade ---\n{c.Source}\n\n--- c# ---\n{c.CSharpSource}");
        }
        TestContext.WriteLine(c.Vm!.Output);
    }

    [Test]
    public void CoverageSummary()
    {
        var cases = Cases();
        var ran = cases.Count(c => c.SkipReason == null);
        var unsupported = cases.Where(c => c.SkipReason == null && c.TranspileError != null).ToList();
        var noCompile = cases.Where(c => c.SkipReason == null && c.TranspileError == null && c.CompiledType == null).ToList();
        TestContext.WriteLine($"programs: {cases.Count}, runnable on the VM: {ran}, unsupported: {unsupported.Count}, C# compile failures: {noCompile.Count}");
        foreach (var group in unsupported.GroupBy(c => c.TranspileError!.Split('(')[0]).OrderByDescending(g => g.Count()))
        {
            TestContext.WriteLine($"  {group.Count(),4} x {group.Key}");
        }
        foreach (var c in noCompile.Take(10))
        {
            TestContext.WriteLine($"  compile: {c.Name}: {c.CSharpCompileError}");
        }
    }
}

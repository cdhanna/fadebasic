using FadeBasic;

namespace Tests;

public class FormatTests
{
    private Lexer lexer;
    private LexerResults results;
    private TokenFormatSettings settings;
    void GetEdits(string src, out List<TokenFormatEdit> edits, out string applied)
    {
        if (settings == null)
        {
            settings = new TokenFormatSettings
            {
                TabSize = 4,
                Casing = TokenFormatSettings.CasingSetting.Ignore
            };
        }
        lexer = new Lexer();
        results = lexer.TokenizeWithErrors(src, TestCommands.CommandsForTesting);
        edits = TokenFormatter.Format(results.combinedTokens, settings);
        applied = TokenFormatter.ApplyEdits(src, edits);
    }
    
    
    [TestCase("x =  5", "x = 5")]
    [TestCase("x =5", "x = 5")]
    [TestCase("x=5", "x = 5", 2)]
    [TestCase(@"
y =2
x=5
", @"
y = 2
x = 5
", 3)]
    [TestCase("x=4, y=2", "x = 4, y = 2", 4)]
    [TestCase("dim x(3)", "dim x(3)", 0)]
    [TestCase("dim x(3 )", "dim x(3)", 1)]
    [TestCase("dim  x (3)", "dim x(3)", 2)]
    [TestCase("x$ = \" paste\"", "x$ = \" paste\"", 0)]
    [TestCase("x$ =  \" paste\"", "x$ = \" paste\"", 1)]
    [TestCase("x,y", "x, y", 1)]
    [TestCase("function myFunc()", "function myFunc()", 0)]
    [TestCase("function  myFunc()", "function myFunc()", 1)]
    [TestCase("function myFunc ()", "function myFunc()", 1)]
    [TestCase("function myFunc(x,h)", "function myFunc(x, h)", 1)]
    [TestCase("function myFunc( x ,h)", "function myFunc(x, h)", 3)]
    [TestCase("if y > 3 then print 3", "if y > 3 then print 3", 0)]
    [TestCase("if y >3 then print 3", "if y > 3 then print 3", 1)]
    [TestCase("x.y", "x.y", 0)]
    [TestCase("x.y,y.d", "x.y, y.d", 1)]
    [TestCase(
        @"
if y >3 then print 3
x= 3", 
        @"
if y > 3 then print 3
x = 3", 2)]
    public void Format_Spaces(string src, string expected, int editCount=1)
    {
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
    
    [TestCase("if x then y", "IF x THEN y", 2)]
    [TestCase(@"
local x = testFunc(1,2)
function testFunc(a,b)
n = a+b
endfunction n
", @"
LOCAL x = testFunc(1, 2)
FUNCTION testFunc(a, b)
    n = a + b
ENDFUNCTION n
", 8)]
    public void Format_Uppers(string src, string expected, int editCount=1)
    {
        settings = new TokenFormatSettings
        {
            Casing = TokenFormatSettings.CasingSetting.ToUpper,
            TabSize = 4
        };
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
    
    
    [TestCase("IF x then y", "if x then y", 1)]
    [TestCase(@"
LOCAL x = testFunc(1,2)
fUnCtIoN testFunc(a,b)
n = a+b
ENDFUNCTION n
", @"
local x = testFunc(1, 2)
function testFunc(a, b)
    n = a + b
endfunction n
", 8)]
    public void Format_Lowers(string src, string expected, int editCount=1)
    {
        settings = new TokenFormatSettings
        {
            Casing = TokenFormatSettings.CasingSetting.ToLower,
            TabSize = 4
        };
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
    
    
    [TestCase("IF x then y", "IF x then y", 0)]
    [TestCase(@"
local x = testFunc(1,2)
fUnCtIoN testFunc(a,b)
n = a+b
ENDFUNCTION n
", @"
local x = testFunc(1, 2)
fUnCtIoN testFunc(a, b)
    n = a + b
ENDFUNCTION n
", 5)]
    public void Format_CaseIgnore(string src, string expected, int editCount=1)
    {
        settings = new TokenFormatSettings
        {
            Casing = TokenFormatSettings.CasingSetting.Ignore,
            TabSize = 4
        };
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
    
    [TestCase(@"
if x
n
endif
", @"
if x
  n
endif
")]
    public void Format_SpaceSizes(string src, string expected, int editCount=1)
    {
        settings = new TokenFormatSettings
        {
            Casing = TokenFormatSettings.CasingSetting.Ignore,
            TabSize = 2
        };
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
    
    [TestCase(@"
if x
n
endif
", "\nif x\n\tn\nendif\n")]
    [TestCase(@"
if x
if y
n
endif
", "\nif x\n\tif y\n\t\tn\n\tendif\n", 3)]
    public void Format_SpaceTabs(string src, string expected, int editCount=1)
    {
        settings = new TokenFormatSettings
        {
            Casing = TokenFormatSettings.CasingSetting.Ignore,
            TabSize = 4,
            UseTabs = true
        };
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
    
    [TestCase(@"
if 3
x = 2
endif", @"
if 3
    x = 2
endif", 1)]
    [TestCase(@"
 if y
x = 2
endif", @"
if y
    x = 2
endif", 2)]
    [TestCase(@"
if 3
 if 8
x = 2
                  endif
endif", @"
if 3
    if 8
        x = 2
    endif
endif", 3)]
    [TestCase(@"
if 5
 if 8
x = 2
endif",
        @"
if 5
    if 8
        x = 2
    endif", 3)]
    [TestCase(@"
 if 3
  else
n
endif
",
        @"
if 3
else
    n
endif
", 3)]
    [TestCase(@"
 if 3
            if n
print a,b
endif
  else
n
endif
",
        @"
if 3
    if n
        print a, b
    endif
else
    n
endif
", 7)]
    public void Format_Indents_If(string src, string expected, int editCount=1)
    {
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
    
    
    [TestCase(@"
 for x= 1   to 9
x = 2
  next", @"
for x = 1 to 9
    x = 2
next", 5)]
    public void Format_Indents_For(string src, string expected, int editCount=1)
    {
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
    
    [TestCase(@"
function(a,b)
a = b
  endfunction", @"
function(a, b)
    a = b
endfunction", 3)]
    public void Format_Indents_Function(string src, string expected, int editCount=1)
    {
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
    
    
    [TestCase(@"
 while tuna(3)
  b = tuna (1)
  b = c
   endwhile
", @"
while tuna(3)
    b = tuna(1)
    b = c
endwhile
", 5)]
    public void Format_Indents_While(string src, string expected, int editCount=1)
    {
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
    
    
    [TestCase(@"
do
 3
 for n = 1 to 3
5
next
loop
", @"
do
    3
    for n = 1 to 3
        5
    next
loop
", 4)]
    public void Format_Indents_Loop(string src, string expected, int editCount=1)
    {
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
    
    
    [TestCase(@"
repeat
x(2,1)
until y(x)
", @"
repeat
    x(2, 1)
until y(x)
", 2)]
    public void Format_Indents_RepeatUntil(string src, string expected, int editCount=1)
    {
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }

    
    [TestCase(@"
select n
        case 1
 x(1)
  endcase
default
x(2)
endcase
endselect
", @"
select n
    case 1
        x(1)
    endcase
    default
        x(2)
    endcase
endselect
", 6)]
    public void Format_Indents_Select(string src, string expected, int editCount=1)
    {
        GetEdits(src, out var edits, out var res);
        Assert.That(res, Is.EqualTo(expected));
        Assert.That(edits.Count, Is.EqualTo(editCount));
    }
}
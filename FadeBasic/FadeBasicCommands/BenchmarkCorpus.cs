namespace FadeBasic
{
    public static class BenchmarkCorpus
    {
        public const string Short = @"
a = 10
b = 20
c = a + b
d# = 3.14
e# = d# * 2.0
s$ = ""hello world""
result = a * b - c
flag = result > 100
";

        public const string Medium = @"
dim scores(10)
total = 0
for i = 1 to 10
    scores(i) = i * i
    total = total + scores(i)
next i
average = total / 10
if average > 30
    big = 1
else
    big = 0
endif
x# = 1.0
for j = 1 to 20
    x# = x# * 1.05
next j
name$ = ""FadeBasic""
result$ = name$ + "" benchmark""
n = 0
while n < 8
    n = n + 1
endwhile
a = 255
b = a && 15
c = a || 1
d = a XOR b
e = NOT a
acc = 0
for k = 1 to 5
    if k = 3
        acc = acc + k
    endif
next k
";

        public const string Large = @"
type point
    x
    y
endtype

type rect
    left
    top
    right
    bottom
endtype

p as point
p.x = 42
p.y = 17

r as rect
r.left = 0
r.top = 0
r.right = 800
r.bottom = 600

dim pts(20) as point
for i = 0 to 19
    pts(i).x = i * 5
    pts(i).y = i * 3
next i

sumX = 0
sumY = 0
for i = 0 to 19
    sumX = sumX + pts(i).x
    sumY = sumY + pts(i).y
next i

function clamp(v, lo, hi)
    result = v
    if v < lo
        result = lo
    endif
    if v > hi
        result = hi
    endif
endfunction result

function sign(n)
    result = 0
    if n > 0
        result = 1
    endif
    if n < 0
        result = -1
    endif
endfunction result

a = clamp(150, 0, 100)
b = clamp(-5, 0, 100)
s1 = sign(42)
s2 = sign(-7)
s3 = sign(0)

acc# = 0.0
for i = 1 to 50
    v# = i * 0.1
    acc# = acc# + v#
next i

outer = 0
for row = 1 to 10
    for col = 1 to 10
        if row = col
            outer = outer + 1
        endif
    next col
next row

n = 200
while n > 0
    n = n - 7
endwhile

name$ = ""FadeBasic""
version$ = ""1.0.0""
tag$ = name$ + "" v"" + version$
a$ = ""alpha""
b$ = ""beta""
c$ = a$ + b$

x# = 1.0
y# = 1.0
for fib = 1 to 30
    temp# = x#
    x# = y#
    y# = temp# + y#
next fib

dim vals(100)
for i = 0 to 99
    vals(i) = i * i - i + 1
next i
total = 0
for i = 0 to 99
    total = total + vals(i)
next i

flag1 = total > 1000
flag2 = total < 500000
flag3 = flag1 && flag2
combined = flag1 || flag2

base = 2
power = 1
for exp = 1 to 16
    power = power * base
next exp
";
    }
}

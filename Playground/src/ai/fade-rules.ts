// Terse, curated "Fade is not like other BASIC dialects" rules, kept resident
// in the system prompt. This is deliberately SHORT and high-signal — a small
// model attends to a tight list of "do X not Y" far better than to pages of
// reference prose, and it costs ~1/10th the tokens of the language-guide
// extract. Expand this list as new failure modes show up; for full detail the
// model still has search_docs over the language guide.
//
// Every rule here is verified against FadeBook/Language.md. When you add one,
// keep it imperative and contrastive (state the wrong thing AND the right one).

export const FADE_RULES = `CORE FADE SYNTAX RULES (authoritative — Fade differs from other BASIC/C dialects; do NOT guess from them):

1. NO \`elseif\`/\`elif\`. Two IF forms, and they DON'T mix:
   - Single-line: \`IF <expr> THEN <statement>\` (optionally \`… ELSE <statement>\`). It is COMPLETE on that line — do NOT put \`ENDIF\` after it. e.g. \`IF x > 0 THEN PRINT "y" ELSE PRINT "n"\` (no ENDIF).
   - Block: \`IF <expr>\` alone on its line (NOTHING after the condition — no \`THEN <statement>\`), then the body on following lines, then \`ENDIF\`. Use \`ELSE\` for the else branch.
   \`ENDIF\` belongs ONLY to the block form. Writing \`IF x THEN PRINT "y"\` and then \`ENDIF\` is wrong. For more than two branches, NEST block ifs or use SELECT/CASE.
2. Conditions are NUMERIC, not boolean — a positive number is true. There is no \`true\`/\`false\` keyword.
3. Multi-branch: \`SELECT x\` / \`CASE 0\` … \`ENDCASE\` / \`CASE DEFAULT\` … \`ENDCASE\` / \`ENDSELECT\`. CASE takes a constant literal.
4. Loops (each has its OWN closer):
   - \`FOR t = 1 TO 10 STEP 1\` … \`NEXT\`   (STEP optional; STEP can be negative; close with NEXT)
   - \`WHILE <expr>\` … \`ENDWHILE\`
   - \`REPEAT\` … \`UNTIL <expr>\`           (checks at the end)
   - \`DO\` … \`LOOP\`                         (runs forever; break with EXIT)
   Use \`EXIT\` to break a loop, \`SKIP\` to continue to the next iteration. There is no \`break\`/\`continue\`.
   For an infinite loop use \`DO … LOOP\` or \`WHILE 1 … ENDWHILE\` — NOT \`WHILE true\` (there is no \`true\`).
5. Variables: assign a value to declare one, e.g. \`x = 0\`. ALWAYS initialize before use (using a variable before it has a value is an error). Scope with \`GLOBAL x = 3\` or \`LOCAL y = 0\`. A FUNCTION cannot see outer variables unless they are GLOBAL.
6. Functions: \`FUNCTION add(a, b)\` … \`ENDFUNCTION sum\` — the return value goes AFTER \`ENDFUNCTION\`. Early return: \`EXITFUNCTION value\`. Call like \`x = add(1, 2)\`.
7. Comments: a backtick \` starts a line comment (NOT \`//\`, \`#\`, or \`'\`). Block comments: \`REMSTART\` … \`REMEND\`.
8. ANY command that returns a value MUST be called with parentheses — like a function — EVERY time you use its result, even with no arguments and even for multi-word commands. \`mx = mouse x()\`, \`my = mouse y()\`, \`id = reserve sfx clip id(0)\`. Writing the bare name (\`mouse x\`, \`mouse y\`) is WRONG. (Commands that do something but don't return a value are written WITHOUT parens as statements: \`sync\`, \`print x\`.) Don't guess a command's arguments — call search_docs for its signature.
9. Block-enders are ONE word: \`endif\`, \`endwhile\`, \`endfunction\`, \`endselect\`, \`endcase\`, plus \`next\`, \`loop\`, \`until\`. NEVER write them with a space. \`end\` BY ITSELF is a separate command that STOPS the program — so \`end function\` halts the program and leaves a stray \`function\` keyword. Write \`endfunction\` (and \`endif\`, \`endwhile\`, …) as one word.
10. Compound assignment IS supported: \`+=\`, \`-=\`, \`*=\`, \`/=\` are shorthand (e.g. \`x += 1\` means \`x = x + 1\`). The long form works too.
11. A command name is RESERVED — you cannot use it as a variable. \`sprite = sprite(0)\` is invalid because \`sprite\` is a command. Name the variable something else (e.g. \`spr\`, \`ship\`). The same goes for \`text\`, \`box\`, \`sound\`, etc.
12. You CANNOT assign to a command or to a command's result. A value-returning command is READ-ONLY — \`sprite x(1) = 100\` is INVALID (you can't store into \`sprite x\`). To CHANGE state you must call the matching SETTER command. To move a sprite, keep its position in your OWN variables and call \`position sprite\`:
   \`x = 100 : y = 100\`
   \`DO\`
   \`  IF rightKey() THEN x = x + 1\`
   \`  position sprite 1, x, y\`
   \`  sync\`
   \`LOOP\`
   (Read the value with \`mx = mouse x()\`; never write \`mouse x() = …\` or \`sprite x(1) = …\`.)
13. ARRAYS hold many similar values. Declare with \`DIM\` and a size; arrays are GLOBAL by default and indexed from 0: \`DIM enemyX(10)\` then \`enemyX(0) = 100\`. Element type via sigil or \`AS\`: \`DIM scores(10) AS INTEGER\`. Multi-dimensional: \`DIM grid(8, 8)\` → \`grid(x, y)\` (max 5 dimensions). Use an array (with a \`FOR i = 0 TO n … NEXT\` loop) whenever you have several of the same thing — bullets, enemies, particles, tiles — instead of \`x1, x2, x3, …\`.
14. USER-DEFINED TYPES (UDT) group related fields into one record. Declare with \`TYPE Name\` … \`ENDTYPE\`, one field per line (sigil or \`AS\` for field types):
   \`TYPE Ball\`
   \`  x#\`
   \`  y#\`
   \`  vx#\`
   \`  vy#\`
   \`ENDTYPE\`
   Make a variable with \`LOCAL b AS Ball\` (or \`GLOBAL\`), access fields with a dot: \`b.x# = 100\`. Combine with arrays for many entities: \`DIM balls(10) AS Ball\` then \`balls(0).x# = 5\`. Use a UDT when ONE thing has several attributes that travel together (a ball's position + velocity); reach for it instead of parallel arrays or loose variables.`;

# Playground testing strategy

This is how I keep the Playground stable while making non-trivial changes
to it. The headline: I drive a real Chromium against the live dev server
through Playwright, and assert against **observable, page-level state** —
DOM, Monaco models, the worker bridge — never against internal source
positions or stack snapshots. Each user-facing change ships with one or
two small probes in this style, and the existing suites stay green.

## Why not unit tests?

The Playground is dominated by integration concerns: WASM ↔ worker
↔ Monaco ↔ dockview ↔ OPFS. Most bugs live at those seams, not inside any
one module's logic. A unit test of `parseFadeProject()` catches almost
nothing real; a headless probe that types into the editor, edits
`fade.json`, and clicks the Run button catches the bug where the
project's source list isn't being honored at compile time. So the test
budget goes there.

There's still a place for unit testing (the JSON path locator is a
candidate — pure function, lots of edge cases), but the default is
end-to-end through the live page.

## The test runner

Each suite is a single `.mjs` file under `scripts/`. It uses Playwright
to launch headless Chromium, points it at `http://localhost:5311/`, waits
for `window.__fadeBootstrapDone`, runs a list of `test(...)` cases
serially, and prints `OK/FAIL` per case + a final count. Exit code 0 on
success.

```
scripts/
  test-lsp.mjs            ← Monaco-side LSP behavior (hover, completion, …)
  test-dap.mjs            ← Debug session (start, step, breakpoint, …)
  test-tests-panel.mjs    ← Tests panel UI (filter, run, failure jumps, …)
  test-project.mjs        ← fade.json + project source concat + badges
  test-projects-overlay.mjs ← Project switcher overlay
```

Run a single suite:

```sh
node scripts/test-project.mjs
```

Run them all (typical pre-merge sanity):

```sh
node scripts/test-lsp.mjs       && \
node scripts/test-dap.mjs       && \
node scripts/test-tests-panel.mjs && \
node scripts/test-project.mjs   && \
node scripts/test-projects-overlay.mjs
```

The dev server is expected to be already running on `:5311` (start it
with `npm run dev`).

## The shape of a probe

Every suite follows the same shape:

```js
import { chromium } from 'playwright';

const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ viewport: { width: 1500, height: 950 } });
const page = await ctx.newPage();

// Optional: wipe OPFS so probes start from a known state. Project + overlay
// suites do this; LSP and DAP don't (they don't care about workspace state).
await page.goto('http://localhost:5311/', { waitUntil: 'domcontentloaded' });
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    try { await root.removeEntry('workspace', { recursive: true }); } catch {}
    localStorage.removeItem('fade.activeProject');
});

// Reload twice. First reload kicks bootstrap; second is a "settling" pass
// that avoids Vite/HMR double-bootstrap noise. Always wait for the
// __fadeBootstrapDone flag the page sets at the end of bootstrap().
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
await page.reload({ waitUntil: 'domcontentloaded' });
await page.waitForFunction(() => window.__fadeBootstrapDone, { timeout: 60000 });
await new Promise((r) => setTimeout(r, 1500));   // settling jitter

const tests = [];
function test(name, fn) { tests.push({ name, fn }); }

test('header shows the active project name', async () => {
    const label = (await page.locator('#project-name').textContent()) || '';
    if (!label.trim()) throw new Error('project-name label is empty');
    return { label: label.trim() };           // return value is logged on OK
});

// … more tests …

let passed = 0, failed = 0;
for (const t of tests) {
    process.stdout.write(`• ${t.name} ... `);
    try {
        const r = await t.fn();
        console.log('OK', r ? JSON.stringify(r) : '');
        passed++;
    } catch (e) {
        console.log('FAIL\n   ', e.message);
        failed++;
    }
}
await browser.close();
console.log(`\n${passed} passed, ${failed} failed`);
process.exit(failed === 0 ? 0 : 1);
```

A passing probe returns a small object that gets JSON-logged next to
`OK`. That's intentional — when a CI run prints `OK {"badges":[{"file":
"main.fbasic","text":"1","listed":true,"orphan":false}, …]}`, future me
can read the run log and *see* what the probe was actually asserting,
not just "it passed." Failures throw `new Error(...)` with a concrete
message including the unexpected value.

Each test is one assertion of intent. They share a Page, so order
matters and side effects carry forward; embrace that, but don't rely on
order beyond what you can read in the file.

## Driving the page

There are three levers, in order of preference:

### 1. Page-exposed test helpers (`window.__fade*`)

Where possible, the page exposes a small typed surface for tests:

| global | what |
|---|---|
| `window.monaco` | the full Monaco API |
| `window.__fadeBootstrapDone` | flips `true` once bootstrap completes |
| `window.__fadeDockview` | dockview API (panels, setActive, getPanel) |
| `window.__fadeRunnerHelpers` | direct worker calls — `listTests`, `runTests`, `project.getSource`, `debug.*` |
| `window.__fadeLspProbe(method, params)` | route a Monaco-bypassing LSP call to the worker |
| `window.__debugLastEvent` | last debug event the page received (poll-able) |
| `window.forceHardReset` | console-only OPFS wipe + reload |

These let probes call the same code paths a real user would trigger,
without hand-driving DOM widgets that already work. Example:

```js
const r = await page.evaluate(({ source }) =>
    window.__fadeRunnerHelpers.runTests({ source }),
    { source: 'test foo\n    assert 1 + 1 = 2\nendtest\n' },
);
```

### 2. Monaco-level actions

When the test cares about Monaco-side behavior (cursor placement,
hover widgets, semantic tokens, registered actions):

```js
// seed the active model directly
await page.evaluate(({ src }) => {
    const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
    ed.getModel().applyEdits([{ range: ed.getModel().getFullModelRange(), text: src }]);
}, { src });

// place cursor
await page.evaluate(({ line, col }) => {
    const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
    ed.setPosition({ lineNumber: line, column: col });
    ed.focus();
}, { line: 8, col: 1 });

// trigger a registered action (e.g. our context-menu Run Test at Cursor)
await page.evaluate(() => {
    const ed = window.monaco.editor.getEditors().find((e) => e.getModel()?.getLanguageId() === 'fade');
    return ed.getAction('fade.runTestAtCursor').run();
});

// inspect markers from the model
const markers = await page.evaluate(() => {
    const uri = window.monaco.Uri.file('/workspace/fade.json');
    return window.monaco.editor.getModelMarkers({ resource: uri }).map((m) => ({
        owner: m.owner, severity: m.severity, message: m.message, line: m.startLineNumber,
    }));
});
```

### 3. DOM clicks for genuinely UI-level concerns

For things the user *sees*, drive the DOM:

```js
await page.locator('#new-file').click();
await page.locator('#tests-search').fill('addsone');
await page.locator('#project-list .project-row').first().click();
```

Use these for click-targets, focus, visibility, and keyboard shortcuts.
Avoid them as a way to wire up business logic — the helpers above are
faster and far less flaky.

## Handling JS dialogs (prompt / alert / confirm)

The Playground uses native `prompt()`/`alert()` for New-File + a few
guards. Playwright intercepts those with `page.on('dialog', …)`. Two
patterns:

```js
// Single dialog, single response:
page.once('dialog', (d) => d.accept('helper.fbasic'));
await page.locator('#new-file').click();

// Chain: prompt followed by an alert (e.g. fade.json refusal path)
const handler = async (d) => {
    if (d.type() === 'prompt') await d.accept('fade.json');
    else { alertText = d.message(); await d.accept(); }
};
page.on('dialog', handler);
await page.locator('#new-file').click();
await new Promise((r) => setTimeout(r, 800));
page.off('dialog', handler);
```

`page.once` fires for the *next* dialog and unbinds itself. If a second
dialog follows (as with the fade.json reject path: prompt → alert), use
`page.on` + `page.off` so you don't double-bind.

## Waiting

Three flavors, ordered by reliability:

```js
// Best: poll for a specific page-level fact
await page.waitForFunction(() =>
    document.querySelectorAll('#tests-log .tests-log-line.fail').length > 0,
    { timeout: 8000 });

// Good: wait for a selector to (dis)appear
await page.waitForSelector('#project-overlay:not([hidden])', { timeout: 3000 });

// Last resort: fixed-time settle
await new Promise((r) => setTimeout(r, 800));
```

Fixed sleeps are tolerable for "let the debounced save timer flush" (we
have a 600ms one) and "let HMR settle on first boot". Anywhere else,
prefer `waitForFunction`/`waitForSelector` — they're the difference
between a flaky probe and a stable one.

For hidden elements (which Playwright considers "not visible" by
default), use `waitForFunction` against the `hidden` attribute directly
rather than `waitForSelector('#x[hidden]')` — the latter requires
visibility.

## Asserting

- Read **page-observable values**: DOM text, attributes, `getModelMarkers()`,
  `getModel().getValue()`, `getPosition()`, return values from worker
  helpers. Never reach into module internals.
- Compare loosely on text, strictly on counts/positions: `/util\.fbasic/.test(label)`
  is fine for headers, `markers.length !== 0` is fine, but cursor
  position should be an exact line number.
- When asserting on Monaco markers, **filter by owner** —
  `m.owner === 'fade-config'`, `m.owner === 'fade'` (LSP), etc. — so
  cross-source diagnostics don't poison the assertion.
- Capture page errors during the part you care about and assert no
  unwanted exceptions fired:

  ```js
  const errs = [];
  const handler = (e) => errs.push(e.message);
  page.on('pageerror', handler);
  /* do the thing */
  page.off('pageerror', handler);
  if (errs.find((m) => /already exists/.test(m))) throw new Error(...);
  ```

## OPFS hygiene

Probes that exercise the project system should reset OPFS at the top so
prior runs don't bleed in. The shape:

```js
await page.evaluate(async () => {
    const root = await navigator.storage.getDirectory();
    try { await root.removeEntry('workspace', { recursive: true }); } catch {}
    localStorage.removeItem('fade.activeProject');
});
await page.reload({ waitUntil: 'domcontentloaded' });
```

This is essentially what `window.forceHardReset()` does. Suites that
don't touch OPFS (LSP, DAP) skip this step and run faster.

When the probe edits a file that needs to persist (e.g. `fade.json`
edits that need to land in OPFS before a reload), **open the file in a
tab first** so the page's `model.onDidChangeContent` save listener is
attached — without that, `model.applyEdits()` only changes the in-memory
model, never the OPFS file:

```js
await page.locator('#file-list li[data-name="fade.json"]').click();
await new Promise((r) => setTimeout(r, 200));
await page.evaluate(() => {
    const m = window.monaco.editor.getModels().find((mm) => mm.uri.toString().endsWith('/fade.json'));
    m.applyEdits([{ range: m.getFullModelRange(), text: '...' }]);
});
await new Promise((r) => setTimeout(r, 1200));   // save-timer is 600ms; 1.2s is a safe buffer
```

Many of our "wait" calls are sized against the **page's 600ms
auto-save debounce**. If you bump that constant, bump the waits.

## Snapshot screenshots

Visual regressions don't get committed as fixtures (they rot fast and
diff badly across machines). When a feature is design-heavy (badges,
overlays, markdown preview, output panel styling), I keep a one-off
snapshot script in `scripts/_snap-*.mjs`, run it, eyeball the PNG, then
delete the script. The patterns inside are the same as a probe — set up
state, click around, `page.screenshot({ path: '/tmp/xyz.png' })`,
inspect manually.

Leaving them in `scripts/` permanently turns into bit-rot. Treat them as
disposable.

## What I do when something fails

1. **Re-run alone first** — `node scripts/test-project.mjs` is cheap
   and rules out cross-suite contamination.
2. **Re-run with `headless: false`** so I can watch what Playwright
   actually does. Edit the suite's `chromium.launch({ headless: true })`
   and re-run; the page opens and stops on the failing assertion.
3. **Run a diagnostic** — a one-off `scripts/_diag-*.mjs` that
   reproduces just the failing step and dumps a structured object
   (`JSON.stringify(state, null, 2)`). Delete when done. Several of
   the bugs in this codebase were caught this way (e.g. the wrong-file-
   opens-by-default bug surfaced via a diag script that printed the
   active model URI alongside the visible `view-line` token classes).
4. **Add a probe** — once the fix lands, write a new `test(...)` case
   in the relevant suite asserting the now-correct behavior. The probe
   should fail against the broken code and pass against the fix; if it
   passes both ways, it's not testing anything.

## Adding a new suite

Copy `scripts/test-project.mjs` as a template, change the URL/setup if
needed, replace the tests, run it. Don't try to share infrastructure
between suites until you have three of them — premature abstractions in
test code make the failure modes harder to read.

## Bridge / WebRuntime changes

Probes for behavior that depends on `WebRuntime/FadeBridge.cs` need a
fresh runtime build. `node scripts/build-runtime.mjs` runs
`dotnet publish` and copies the WASM bundle into
`public/runtime/`. If you change `FadeBridge.cs` and your tests don't
seem to pick up the change, you forgot this step.

## Current test counts

| Suite | Tests |
|---|---|
| `test-lsp.mjs` | 17 |
| `test-dap.mjs` | 11 |
| `test-tests-panel.mjs` | 5 |
| `test-project.mjs` | 30 |
| `test-projects-overlay.mjs` | 9 |
| `test-help.mjs` | 6 |
| **Total** | **78** |

Keep this rough count + the suite list current when you add or move
files; it's a quick sanity check after a big change ("did I just delete
a probe by accident?").

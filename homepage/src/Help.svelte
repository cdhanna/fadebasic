<script>
  // Registering the components defines <fade-runnable>/<fade-code>. The content
  // (blocks + table of contents) is generated from Language.md by
  // @fadebasic/md-to-svelte at build time.
  import '@fadebasic/components';
  import { HELP_BLOCKS, HELP_TITLE, HELP_TOC } from './generated/help-content.js';
  import { onMount, tick } from 'svelte';
  import { cubicOut } from 'svelte/easing';

  const GITHUB = 'https://github.com/cdhanna/fadebasic/tree/main?tab=readme-ov-file#fade-basic';
  const DISCORD = 'https://discord.gg/yxFAFJurvU';

  // Scroll-spy: which section is currently in view. Drives the moving left-
  // border indicator in the TOC (like VSCode's minimap position). Updated on
  // scroll — the topmost heading whose top has passed a small offset wins.
  let activeSlug = $state(HELP_TOC[0]?.slug ?? null);
  $effect(() => {
    const heads = HELP_TOC.map((t) => document.getElementById(t.slug)).filter(Boolean);
    if (!heads.length) return;
    const spy = () => {
      let cur = heads[0].id;
      for (const h of heads) { if (h.getBoundingClientRect().top <= 130) cur = h.id; else break; }
      if (cur !== activeSlug) {
        activeSlug = cur;
        document.querySelector(`.toc-list a[data-slug="${cur}"]`)?.scrollIntoView({ block: 'nearest' });
      }
    };
    spy();
    window.addEventListener('scroll', spy, { passive: true });
    window.addEventListener('resize', spy);
    return () => { window.removeEventListener('scroll', spy); window.removeEventListener('resize', spy); };
  });

  // Only ONE snippet may be "live" (upgraded to a full editor) at a time —
  // clicking "try it" elsewhere swaps it, unmounting the previous editor.
  let active = $state(null);
  let copied = $state(null);
  let copyTimer;

  function copy(code, i) {
    navigator.clipboard?.writeText(code);
    copied = i;
    clearTimeout(copyTimer);
    copyTimer = setTimeout(() => (copied = null), 1200);
  }

  // The section anchor embedded in the hash-router hash: everything after the
  // SECOND `#`. "#/help#variables" → "variables"; "#/help" → "".
  function currentAnchor() {
    const h = location.hash.slice(1);       // "/help#variables"
    const i = h.indexOf('#');
    return i >= 0 ? decodeURIComponent(h.slice(i + 1)) : '';
  }

  function scrollToAnchor(smooth) {
    const id = currentAnchor();
    if (!id) return;
    const el = document.getElementById(id);
    if (el) { el.scrollIntoView({ behavior: smooth ? 'smooth' : 'auto', block: 'start' }); activeSlug = id; }
  }

  // Point the URL bar at a section (route + in-page anchor) and copy the link.
  // Setting the hash fires hashchange, which scrolls us there.
  function linkToSection(id) {
    location.hash = `/help#${id}`;
    navigator.clipboard?.writeText(location.href);
    showToast('Link copied');
  }

  let toast = $state('');
  let toastTimer;
  function showToast(msg) { toast = msg; clearTimeout(toastTimer); toastTimer = setTimeout(() => (toast = ''), 1400); }

  // TOC links carry the full hash (`#/help#slug`), so clicking one updates the
  // hash → hashchange → scroll. Highlight immediately without waiting for spy.
  function goto(slug) { activeSlug = slug; }

  onMount(() => {
    // GitHub-style hover anchor on every heading: click copies a link to it.
    for (const h of document.querySelectorAll('.help-body h1[id], .help-body h2[id], .help-body h3[id], .help-body h4[id]')) {
      const a = document.createElement('a');
      a.className = 'heading-anchor';
      a.href = `#/help#${h.id}`;
      a.title = 'Copy link to this section';
      a.setAttribute('aria-label', 'Copy link to this section');
      a.textContent = '#';
      a.addEventListener('click', (e) => { e.preventDefault(); linkToSection(h.id); });
      h.appendChild(a);
    }
    // Deep link on load, then keep in sync as the hash changes.
    scrollToAnchor(false);
    const onHash = () => scrollToAnchor(true);
    window.addEventListener('hashchange', onHash);
    return () => window.removeEventListener('hashchange', onHash);
  });

  // ── Responsive tiers ──────────────────────────────────────────────────
  // Wide: fixed TOC + a reserved debug-sidebar gap. Narrower: the TOC folds out
  // from the side (drawer) so the content reclaims the width. Too small for the
  // debugger: disable try-it entirely (snippets stay static, copy-only).
  let tocDrawer = $state(false);   // TOC is a fold-out drawer at this width?
  let tocOpen = $state(false);     // ...and is it open?
  let canTryIt = $state(true);     // enough room to run the IDE?
  if (typeof window !== 'undefined') {
    const drawerMq = window.matchMedia('(max-width: 1160px)');
    const tinyMq = window.matchMedia('(max-width: 820px)');
    const sync = () => { tocDrawer = drawerMq.matches; canTryIt = !tinyMq.matches; if (!tocDrawer) tocOpen = false; };
    sync();
    drawerMq.addEventListener('change', sync);
    tinyMq.addEventListener('change', sync);
  }

  // ── try-it animation: facade FLIP ─────────────────────────────────────
  // Monaco renders behind a line-number/breakpoint gutter, and its pane is far
  // taller than a one-line snippet — so a shared-element (View Transition) morph
  // both misaligns the code and stretches it (the "pop"). Instead we animate a
  // lightweight CLONE of the static code — a facade, no Monaco, no async: mount
  // the editor with its code hidden, measure where Monaco actually put the first
  // line, glide the facade there (translate ONLY — never scales, so no stretch),
  // then reveal Monaco underneath (pixel-aligned) and drop the facade.
  const reduced = () => typeof window !== 'undefined' && window.matchMedia('(prefers-reduced-motion: reduce)').matches;
  const canAnimate = () => !reduced() && typeof Element.prototype.animate === 'function';

  // The editor's first line for the ORIGINAL code — Monaco renders view-lines
  // absolutely (DOM order ≠ visual order), so sort by top and skip any injected
  // hint-comment lines. `injected` is 1 when a hint comment is prepended.
  const codeLines = (pane) => [...(pane?.querySelectorAll('.view-line') ?? [])]
    .sort((a, b) => a.getBoundingClientRect().top - b.getBoundingClientRect().top);

  async function waitFor(getEl, timeoutMs = 1200) {
    const start = performance.now();
    while (performance.now() - start < timeoutMs) {
      const el = getEl();
      if (el && el.textContent.trim()) return el;
      await new Promise((r) => requestAnimationFrame(r));
    }
    return getEl();
  }

  // A bare text-layer clone of the static code block — no box, no border, font
  // pinned to Monaco's (via the :global(.snip-facade code) rule). Positioned
  // fixed at `rect`; the caller translates it.
  // `codeRect` is the target position for the code TEXT (the <code>, not the
  // <pre>). The facade lives on document.body, so it doesn't inherit the
  // .snip-static padding override — we zero its padding and pin it at the code
  // rect so its text lands exactly where the real code sits.
  function makeFacade(pre, codeRect) {
    const facade = pre.cloneNode(true);
    facade.className = 'fade-code__pre snip-facade';
    Object.assign(facade.style, {
      position: 'fixed', margin: '0', padding: '0',
      left: `${codeRect.left}px`, top: `${codeRect.top}px`, width: `${pre.getBoundingClientRect().width}px`,
      zIndex: '60', pointerEvents: 'none',
      background: 'transparent', boxShadow: 'none', borderRadius: '0', border: '0',
      willChange: 'transform',
    });
    document.body.appendChild(facade);
    return facade;
  }

  // Hand off with a clean hard swap: the facade lands pixel-aligned on the real
  // text (measured diff 0), so remove it the moment the real text is revealed.
  // (A cross-fade instead left the transformed — GPU sub-pixel — facade briefly
  // overlapping the crisp Monaco text, which read as a faint ~1px doubling.)
  function fadeOutFacade(facade) {
    facade.remove();
  }

  // The colored code HTML as shown in the static snippet at open time (which
  // has been LSP-upgraded — command words like `print` are colored). Reused on
  // close: a freshly re-rendered static only has the instant highlighter until
  // its async LSP upgrade, so command tokens would flash white otherwise.
  const coloredCode = new Map();

  async function openIt(i, e) {
    const snip = e.currentTarget.closest('.snip');
    const pre = snip?.querySelector('.fade-code__pre');
    if (!pre || !canAnimate()) { active = i; return; }
    const injected = hintLines(HELP_BLOCKS[i]).length;
    coloredCode.set(i, pre.querySelector('code')?.innerHTML ?? '');

    const facade = makeFacade(pre, (pre.querySelector('code') ?? pre).getBoundingClientRect());

    active = i;                       // mount the editor…
    await tick();
    const live = snip.querySelector('.snip-live');
    const pane = snip.querySelector('.fade-runnable__pane-editor');
    live?.classList.add('snip-anim'); // …with its code hidden while the facade stands in
    await waitFor(() => codeLines(pane)[injected]);

    const target = codeLines(pane)[injected];
    const facadeCode = facade.querySelector('code') ?? facade;
    if (target) {
      const rm = target.getBoundingClientRect();
      const fc = facadeCode.getBoundingClientRect();
      // Sub-pixel accurate — rounding left a 1px gap at the handoff.
      const dx = rm.left - fc.left;
      const dy = rm.top - fc.top;
      try {
        await facade.animate(
          [{ transform: 'translate(0,0)' }, { transform: `translate(${dx}px, ${dy}px)` }],
          { duration: 300, easing: 'cubic-bezier(0.2, 0.75, 0.2, 1)', fill: 'forwards' },
        ).finished;
      } catch { /* animation cancelled */ }
    }
    live?.classList.remove('snip-anim');
    await fadeOutFacade(facade);
  }

  // Reverse of openIt: glide the code from its editor position back to the
  // static snippet spot while the editor chrome fades out (see `editorFade`).
  async function closeIt(e) {
    const snip = e?.currentTarget?.closest('.snip');
    const pane = snip?.querySelector('.fade-runnable__pane-editor');
    const closingIndex = active;
    const injected = hintLines(HELP_BLOCKS[active]).length;
    const fromLine = pane ? codeLines(pane)[injected] : null;
    const live = snip?.querySelector('.snip-live');
    if (!snip || !fromLine || !live || !canAnimate()) { active = null; return; }

    const rm = fromLine.getBoundingClientRect();     // where the code sits now
    live.classList.add('snip-anim');                 // hide Monaco's code; the facade carries it
    // Pin the editor out of flow FIRST, so the static snippet reclaims the
    // space synchronously and we measure its FINAL position (not one still
    // pushed down by the not-yet-removed editor). `out:editorFade` then just
    // fades this pinned pane while the facade morphs the code back over it.
    const lr = live.getBoundingClientRect();
    Object.assign(live.style, {
      position: 'fixed', left: `${lr.left}px`, top: `${lr.top}px`,
      width: `${lr.width}px`, height: `${lr.height}px`, margin: '0', zIndex: '2',
    });

    active = null;                                   // editor fades out; static reflows into place
    await tick();

    await waitFor(() => snip.querySelector('.snip-static .fade-code__pre code'));
    const pre = snip.querySelector('.snip-static .fade-code__pre');
    if (!pre) return;
    // Restore the LSP-colored HTML captured at open time so command tokens
    // don't flash white (the fresh static only has instant-highlighter colors
    // until its async upgrade). This colors both the facade clone and the
    // revealed static.
    const codeEl = pre.querySelector('code') ?? pre;
    const cached = coloredCode.get(closingIndex);
    if (cached) codeEl.innerHTML = cached;
    // Clone WHILE the static code is still visible (else the clone inherits
    // visibility:hidden and the facade never shows), pinned at the code box so
    // its text lands exactly on the real code. THEN hide the real code.
    const facade = makeFacade(pre, codeEl.getBoundingClientRect());
    // Hide only the CODE, not the whole <pre> — the pre's dark background must
    // stay visible so the fading editor reveals dark, not the white page behind
    // it (which read as a white flash).
    codeEl.style.visibility = 'hidden';
    const fc = (facade.querySelector('code') ?? facade).getBoundingClientRect();
    const dx = rm.left - fc.left, dy = rm.top - fc.top;
    try {
      await facade.animate(
        [{ transform: `translate(${dx}px, ${dy}px)` }, { transform: 'translate(0, 0)' }],
        { duration: 280, easing: 'cubic-bezier(0.2, 0.75, 0.2, 1)', fill: 'forwards' },
      ).finished;
    } catch { /* cancelled */ }
    codeEl.style.visibility = '';
    await fadeOutFacade(facade);
  }

  // Editor exit: the pane is already pinned fixed (out of flow) by closeIt, so
  // just fade it while the code morphs back over the top.
  function editorFade() {
    return { duration: 240, easing: cubicOut, css: (t) => `opacity:${t};` };
  }

  // Contextual guidance shown inside a live editor. An explicit `fade:hint`
  // (authored as a hidden comment in Language.md) wins; otherwise, snippets
  // that don't print anything get a nudge to PRINT or set a breakpoint.
  const NUDGE = "This snippet doesn't print anything — add a PRINT statement, or set a breakpoint in the gutter and hit Debug to inspect the values.";
  const hintFor = (block) => block.hint ?? (/\bprint\b/i.test(block.code) ? null : NUDGE);
  // Word-wrap the hint to short lines so, injected as `-comments, it reads as a
  // few tidy lines rather than one long one. Returns [] when there's no hint.
  function wrapHint(text, max = 50) {
    const lines = [];
    let cur = '';
    for (const w of text.split(/\s+/).filter(Boolean)) {
      if (cur && cur.length + 1 + w.length > max) { lines.push(cur); cur = w; }
      else cur = cur ? `${cur} ${w}` : w;
    }
    if (cur) lines.push(cur);
    return lines;
  }
  const hintLines = (block) => { const h = hintFor(block); return h ? wrapHint(h) : []; };
</script>

<div class="help-layout" class:toc-drawer={tocDrawer} class:toc-open={tocOpen}>
  {#if tocDrawer}
    <button class="toc-toggle" aria-label="Table of contents" onclick={() => (tocOpen = !tocOpen)}>
      <span class="codicon codicon-list-unordered"></span>
    </button>
    {#if tocOpen}<button class="toc-scrim" aria-label="Close" onclick={() => (tocOpen = false)}></button>{/if}
  {/if}
  <nav class="toc" onclick={() => { if (tocDrawer) tocOpen = false; }}>
    <div class="toc-actions">
      <a class="toc-home" href="#/">← Home</a>
      <a href={GITHUB} target="_blank" rel="noreferrer">GitHub</a>
      <a href={DISCORD} target="_blank" rel="noreferrer">Discord</a>
    </div>
    <div class="toc-title">{HELP_TITLE ?? 'Fade Language'}</div>
    <ul class="toc-list">
      {#each HELP_TOC as item}
        <li class="lvl-{item.level}">
          <a href="#/help#{item.slug}" data-slug={item.slug} class:active={item.slug === activeSlug}
             onclick={() => goto(item.slug)}>{item.text}</a>
        </li>
      {/each}
    </ul>
  </nav>

  <article class="help-body">
    {#each HELP_BLOCKS as block, i}
      {#if block.type === 'html'}
        {@html block.html}
      {:else if block.runnable}
        <div class="snip">
          {#if active === i}
            <div class="snip-live" out:editorFade>
              <fade-runnable class="ide" layout="ide" debug hide-run closable asset-base="/fade/" code={block.code} hint={hintLines(block).join('\n')} onfadeclose={(e) => closeIt(e)}></fade-runnable>
            </div>
          {:else}
            <div class="snip-static">
              {#if canTryIt}
                <button class="snip-btn try" onclick={(e) => openIt(i, e)}>
                  <span class="codicon codicon-play"></span> try it
                </button>
              {/if}
              <button class="snip-btn copy" title="Copy code" aria-label="Copy code" onclick={() => copy(block.code, i)}>
                <span class="codicon codicon-{copied === i ? 'check' : 'copy'}"></span>
              </button>
              <fade-code asset-base="/fade/" code={block.code} commands={block.commands?.join(",")}></fade-code>
            </div>
          {/if}
        </div>
      {:else}
        <div class="snip">
          <div class="snip-static">
            <button class="snip-btn copy" title="Copy code" aria-label="Copy code" onclick={() => copy(block.code, i)}>
              <span class="codicon codicon-{copied === i ? 'check' : 'copy'}"></span>
            </button>
            <fade-code asset-base="/fade/" code={block.code} commands={block.commands?.join(",")}></fade-code>
          </div>
        </div>
      {/if}
    {/each}
  </article>
  {#if toast}<div class="help-toast">{toast}</div>{/if}
</div>

<style>
  .help-layout {
    padding-left: 240px;   /* reserve room for the fixed TOC */
    text-align: left;
  }

  /* Table of contents — fixed to the LEFT EDGE of the screen (not the centered
     content), so it stays put as the body scrolls. */
  .toc {
    position: fixed;
    left: 0;
    top: 0;
    width: 240px;
    height: 100vh;
    overflow-y: auto;
    box-sizing: border-box;
    padding: 1.25rem 0.75rem 2rem;
    border-right: 1px solid #333;
    font-size: 0.9rem;
    background: #242424;
    z-index: 5;
  }
  .toc-actions { display: flex; flex-wrap: wrap; gap: 0.5rem; margin-bottom: 1rem; }
  .toc-actions a {
    flex: 1 1 auto;
    text-align: center;
    text-decoration: none;
    color: #cfcfcf;
    background: #2d2d2d;
    border: 1px solid #3a3a3a;
    border-radius: 6px;
    padding: 5px 8px;
    font-size: 0.82rem;
  }
  .toc-actions a:hover { background: #3a3d41; color: #fff; }
  .toc-actions .toc-home { background: #0e639c; border-color: #0e639c; color: #fff; }
  .toc-actions .toc-home:hover { background: #1177bb; }
  .toc-title { font-weight: 700; margin: 0.25rem 0 0.5rem; color: #eee; }
  .toc-list { list-style: none; margin: 0; padding: 0; }
  .toc-list li { margin: 1px 0; }
  .toc-list a {
    display: block;
    color: #9aa0a6;
    text-decoration: none;
    padding: 3px 6px;
    border-radius: 4px;
    border-left: 2px solid transparent;
  }
  .toc-list a:hover { color: #fff; background: #2a2d2e; }
  /* Moving position indicator — the section currently in view. */
  .toc-list a { transition: border-left-color 0.15s, color 0.15s; }
  .toc-list a.active { border-left-color: #2f81f7; color: #e9edf1; font-weight: 600; }
  .toc-list .lvl-1 { font-weight: 600; margin-top: 0.4rem; }
  .toc-list .lvl-2 a { padding-left: 6px; }
  /* Nested subsections (the docs use #### under ##). */
  .toc-list .lvl-3 a,
  .toc-list .lvl-4 a { padding-left: 22px; font-size: 0.82rem; color: #808690; }

  .help-body {
    /* Left the prose a debug-sidebar's width to the right of the TOC, so when a
       snippet expands the sidebar fades into that gap and the code column stays
       put (moves vertically + widens, no horizontal slide). */
    margin-left: 268px;         /* 260px sidebar + 8px gap */
    max-width: 760px;
    padding: 1.5rem 0 5rem;
    line-height: 1.55;
  }
  .help-body :global(h1), .help-body :global(h2), .help-body :global(h3), .help-body :global(h4) { margin-top: 1.8rem; scroll-margin-top: 1rem; position: relative; }

  /* GitHub-style section anchor: a `#` that fades in on heading hover, sits in
     the left margin, and copies a link to the section when clicked. */
  :global(.help-body h1 .heading-anchor),
  :global(.help-body h2 .heading-anchor),
  :global(.help-body h3 .heading-anchor),
  :global(.help-body h4 .heading-anchor) {
    position: absolute; left: -0.9em; top: 0;
    opacity: 0; transition: opacity 0.12s;
    color: #6b7280; font-weight: 400; text-decoration: none;
    padding-right: 0.35em; cursor: pointer;
  }
  :global(.help-body h1:hover .heading-anchor),
  :global(.help-body h2:hover .heading-anchor),
  :global(.help-body h3:hover .heading-anchor),
  :global(.help-body h4:hover .heading-anchor),
  :global(.help-body .heading-anchor:focus) { opacity: 1; }
  :global(.help-body .heading-anchor:hover) { color: #2f81f7; }

  .help-toast {
    position: fixed; bottom: 22px; left: 50%; transform: translateX(-50%);
    background: #0e639c; color: #fff; padding: 7px 16px; border-radius: 6px;
    font-size: 0.85rem; z-index: 80; box-shadow: 0 4px 16px rgba(0,0,0,0.4);
    animation: help-toast-in 0.16s ease;
  }
  @keyframes help-toast-in { from { opacity: 0; transform: translate(-50%, 6px); } to { opacity: 1; transform: translate(-50%, 0); } }
  .help-body :global(pre) { background: #1e1e1e; color: #ddd; padding: 0.75rem; border-radius: 6px; overflow-x: auto; }
  .help-body :global(code) { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
  .help-body :global(table) { border-collapse: collapse; }
  .help-body :global(td), .help-body :global(th) { border: 1px solid #444; padding: 4px 8px; }
  /* Don't set display on fade-runnable — the component owns it (grid in IDE
     mode); an override here would collapse the IDE layout to a block stack. */
  .help-body :global(fade-code) { display: block; }

  /* GitHub-style alert callouts (> [!NOTE] / [!TIP] / [!WARNING] …). */
  .help-body :global(.fade-callout) {
    border: 1px solid #30363d;
    border-left-width: 4px;
    border-radius: 6px;
    padding: 0.6rem 0.9rem;
    margin: 1.1rem 0;
    background: rgba(255, 255, 255, 0.03);
  }
  .help-body :global(.fade-callout__title) {
    font-weight: 700;
    margin-bottom: 0.25rem;
    display: flex;
    align-items: center;
    gap: 6px;
  }
  .help-body :global(.fade-callout__body > :first-child) { margin-top: 0; }
  .help-body :global(.fade-callout__body > :last-child) { margin-bottom: 0; }
  .help-body :global(.fade-callout--note)      { border-left-color: #2f81f7; }
  .help-body :global(.fade-callout--note .fade-callout__title)      { color: #2f81f7; }
  .help-body :global(.fade-callout--note .fade-callout__title::before)      { content: "ℹ️"; }
  .help-body :global(.fade-callout--tip)       { border-left-color: #3fb950; }
  .help-body :global(.fade-callout--tip .fade-callout__title)       { color: #3fb950; }
  .help-body :global(.fade-callout--tip .fade-callout__title::before)       { content: "💡"; }
  .help-body :global(.fade-callout--important) { border-left-color: #a371f7; }
  .help-body :global(.fade-callout--important .fade-callout__title) { color: #a371f7; }
  .help-body :global(.fade-callout--important .fade-callout__title::before) { content: "❗"; }
  .help-body :global(.fade-callout--warning)   { border-left-color: #d29922; }
  .help-body :global(.fade-callout--warning .fade-callout__title)   { color: #d29922; }
  .help-body :global(.fade-callout--warning .fade-callout__title::before)   { content: "⚠️"; }
  .help-body :global(.fade-callout--caution)   { border-left-color: #f85149; }
  .help-body :global(.fade-callout--caution .fade-callout__title)   { color: #f85149; }
  .help-body :global(.fade-callout--caution .fade-callout__title::before)   { content: "🛑"; }

  /* Snippets: simple markdown code view + floating try-it / copy affordances. */
  .snip { position: relative; margin: 1.1rem 0; }
  .snip-static { position: relative; }
  /* Clean, tight code block (no fake gutter). Font matched to Monaco (14px) so
     the facade hand-off doesn't resize. The top strip keeps the corner buttons
     off the first line. Alignment on open is handled by the LAYOUT: the editor
     breaks left so its sidebar + line-number gutter land in the reserved gap,
     leaving the code text at the exact same x. */
  /* No border on the code block: Monaco's code area is borderless, so a 1px
     border here shifts the code down ~1px and shows a stray box during the
     facade glide. The facade clones this, so removing it keeps the code's
     height/position identical from snippet → editor. */
  /* Extra top padding so the floating try-it / copy buttons clear the code. */
  .snip-static :global(.fade-code__pre) { padding-top: 2.9rem; border: 0; }
  .snip-static :global(fade-code code) { font-size: 14px; line-height: 19px; }
  .snip-btn {
    /* app.css sets a global `button { flex-grow: 1 }`; pin so buttons in the
       flex livebar don't stretch. */
    flex: 0 0 auto;
    font: inherit;
    font-size: 12px;
    line-height: 1;
    display: inline-flex;
    align-items: center;
    gap: 4px;
    border: 1px solid #3a3a3a;
    background: #2d2d2d;
    color: #ccc;
    border-radius: 4px;
    padding: 4px 9px;
    cursor: pointer;
  }
  .snip-btn:hover { background: #3a3d41; color: #fff; }
  .snip-btn .codicon { font-size: 13px; }
  .snip-static .try, .snip-static .copy { position: absolute; top: 9px; z-index: 2; }
  .snip-static .try { left: 8px; background: #0e639c; border-color: #0e639c; color: #fff; }
  .snip-static .try:hover { background: #1177bb; }
  /* Copy is an icon-only button that fades in on hover (or keyboard focus). */
  .snip-static .copy { right: 8px; padding: 5px 7px; opacity: 0; transition: opacity 0.12s; }
  .snip-static .copy .codicon { font-size: 14px; }
  .snip:hover .snip-static .copy, .snip-static .copy:focus-visible { opacity: 1; }

  /* ─────────────────────────────────────────────────────────────────────
     TRY-IT ANIMATION — tweak here.
     A facade (clone of the static code) glides onto Monaco's real code
     position (JS: openIt → facade.animate). Meanwhile the editor CHROME fades
     in, and Monaco's own code stays hidden until the facade lands. */
  @keyframes snip-chrome-in { from { opacity: 0; } to { opacity: 1; } }
  .snip-live { animation: snip-chrome-in 260ms ease both; }         /* chrome fade-in duration */
  /* Facade = the moving clone (styled inline in openIt — it's cloned onto
     document.body without this component's scope hash, so scoped rules here
     wouldn't match it; hence :global). Pin its code font to Monaco's exactly —
     same family, size, line box, and disabled ligatures — so the morphing text
     doesn't resize or reshape when it hands off to the real editor. */
  :global(.snip-facade code) {
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace;
    font-size: 14px; line-height: 19px; font-feature-settings: "calt" 0, "liga" 0;
  }
  /* Hide Monaco's rendered code until the facade lands, so we don't see two
     copies of the code during the glide. */
  .snip-live.snip-anim :global(.monaco-editor .view-lines) { opacity: 0; }

  .snip-live {
    /* Break LEFT by the sidebar width into the reserved gap so the code pane
       lands over the static code column (the debugger sidebar fills the gap
       between the TOC and the code). Then fill RIGHT to the far edge so the
       editor is balanced (equal ~8px gutters left and right) rather than
       hugging the left with dead space on the right. */
    /* Break left so the (narrowed) sidebar + Monaco's ~89px line-number gutter
       fall into the reserved gap and the code text lands at the static code's
       exact x. Break ≈ sidebar(180) + gutter(89) − code padding(12). */
    margin-left: -257px;
    width: calc(100vw - 240px - 34px);   /* fill right, balanced gutters */
    border: 1px solid #2b2f36;
    border-radius: 8px;
    overflow: hidden;
    box-shadow: 0 8px 30px rgba(0, 0, 0, 0.4);
    position: relative;                  /* anchor the floating close button */
  }
  /* Narrow the debug sidebar so sidebar + gutter fit the reserved gap (keeps the
     tighter "old" spacing while still landing the code text in place). */
  .snip-live :global(.fade-runnable--ide) { grid-template-columns: 180px minmax(0, 1fr); }
  /* In drawer mode the TOC is an overlay, so there's no fixed left column to
     reserve against and the prose is centered (see below). The fixed -259px
     break would shove the editor off the left edge, so instead pin the editor
     to an 8px viewport gutter and fill the width. 388px = 8px gutter + 380px
     (half of the 760px prose the centered margin adds on each side). */
  .toc-drawer .snip-live { margin-left: calc(388px - 50vw); width: calc(100vw - 16px); }
  /* Full debugger IDE, sized for inline docs (component floor is 480px). */
  .snip-live :global(fade-runnable.ide) { height: min(72vh, 620px); }

  /* ── Fold-out TOC (drawer) — when the viewport is too narrow for a fixed TOC
     + the reserved debug gap. The TOC slides in over the content; a hamburger
     toggles it. The content reclaims the TOC's 240px but keeps the debug gap so
     the editor still aligns. */
  .toc-toggle {
    position: fixed; top: 10px; left: 10px; z-index: 25;
    width: 38px; height: 38px; display: inline-flex; align-items: center; justify-content: center;
    background: #0e639c; color: #fff; border: 0; border-radius: 8px; cursor: pointer;
    box-shadow: 0 2px 10px rgba(0,0,0,0.4);
  }
  .toc-toggle .codicon { font-size: 18px; }
  .toc-scrim { position: fixed; inset: 0; z-index: 15; background: rgba(0,0,0,0.45); border: 0; }
  .help-layout.toc-drawer { padding-left: 0; }
  /* TOC is an overlay now — drop the reserved left gap and center the prose in
     the viewport so reading content doesn't look shoved to the right. (An
     expanding snippet fills the full width instead of the gap; see .snip-live.) */
  .toc-drawer .help-body { margin-left: auto; margin-right: auto; }
  .toc-drawer .toc {
    transform: translateX(-100%);
    transition: transform 0.22s cubic-bezier(0.2,0.75,0.2,1);
    z-index: 20;
    box-shadow: 4px 0 24px rgba(0,0,0,0.5);
  }
  .toc-drawer.toc-open .toc { transform: translateX(0); }

  /* Too small for the debugger: try-it is disabled (see canTryIt), so drop the
     reserved gap and let the prose use the full width. */
  @media (max-width: 820px) {
    .help-body { margin-left: 0; max-width: 100%; padding: 1rem 1rem 4rem; }
    .toc { width: min(80vw, 300px); }
  }
</style>

<script>
  // Registering the components defines <fade-runnable>/<fade-code>. The content
  // (blocks + table of contents) is generated from Language.md by
  // @fadebasic/md-to-svelte at build time.
  import '@fadebasic/components';
  import { formatFadeSource } from '@fadebasic/components';
  import { HELP_BLOCKS, HELP_TITLE, HELP_TOC } from './generated/help-content.js';
  import { HELP_BLOCKS as GAME_BLOCKS, HELP_TITLE as GAME_TITLE, HELP_TOC as GAME_TOC } from './generated/game-content.js';
  import { GAME_COMMAND_GROUPS } from './generated/game-commands.js';
  import { THEMES, applyTheme, getTheme } from './theme.js';
  import { normalizeBeta } from './beta.js';
  import { onMount, tick } from 'svelte';
  import { cubicOut } from 'svelte/easing';

  // Theme picker (top bar). main.js already applied the persisted theme at boot.
  let theme = $state(getTheme());
  function pickTheme(id) { theme = applyTheme(id); }

  // ── Tabs & routing ────────────────────────────────────────────────────────
  // Route shape: `#/learn/<tab>/<anchor>`. The tab is part of the PATH (not a
  // second `#`), and the anchor is the last path segment — JS scrolls to it.
  // Tutorial reuses the exact Language layout (prose + morphing try-it snippets),
  // just fed the Game.md content and pointed at the MonoGame runtime.
  const TABS = [
    { key: 'language', label: 'Language', route: '/learn/language' },
    { key: 'commands', label: 'Commands', route: '/learn/commands' },
    { key: 'tutorial', label: 'Game Tutorial', route: '/learn/tutorial' },
  ];
  const DOCS = {
    language: { blocks: HELP_BLOCKS, toc: HELP_TOC, title: HELP_TITLE ?? 'Fade Language', runtime: '' },
    tutorial: { blocks: GAME_BLOCKS, toc: GAME_TOC, title: GAME_TITLE ?? 'Game Tutorial', runtime: 'monogame' },
  };
  const TAB_KEYS = TABS.map((t) => t.key);

  // Parse `#/learn/<tab>/<anchor>`. Also normalizes the old `#/help` / `#/help/game`
  // links (→ language / commands) so shared URLs keep working.
  function parseHash() {
    let h = location.hash.replace(/^#/, '');            // "/learn/commands/sync"
    if (h.startsWith('/help/game')) return { tab: 'commands', anchor: h.slice('/help/game'.length).replace(/^[/#]/, ''), legacy: true };
    if (h.startsWith('/help'))      return { tab: 'language', anchor: h.slice('/help'.length).replace(/^[/#]/, ''), legacy: true };
    const parts = h.replace(/^\//, '').split('/');       // ["learn","commands","sync"]
    const key = TAB_KEYS.includes(parts[1]) ? parts[1] : 'language';
    const anchor = parts.slice(2).join('/');
    return { tab: key, anchor: anchor ? decodeURIComponent(anchor) : '', legacy: false };
  }
  const routeFor = (tab, anchor) => `#/learn/${tab}${anchor ? `/${encodeURIComponent(anchor)}` : ''}`;

  let tab = $state(parseHash().tab);
  let selectedCmd = $state(null);              // Commands tab: the command whose doc is shown

  // ── Beta gating ────────────────────────────────────────────────────────────
  // The Commands + Game Tutorial tabs aren't public yet. They surface only when
  // (1) you're already on that route (direct/shared links keep working), or
  // (2) `?beta` is present in the URL's *query string*. The query string lives
  // before the `#`, so the browser preserves it across every hash-only route
  // change we make — beta "propagates" through app navigation for free — while
  // editing it out of the URL turns beta off immediately (no sticky latch).
  const BETA_TABS = new Set(['commands', 'tutorial']);
  let betaUnlocked = $state(normalizeBeta());
  // A beta tab shows in the nav when unlocked or when it's the tab we're on.
  let visibleTabs = $derived(TABS.filter((t) => !BETA_TABS.has(t.key) || betaUnlocked || tab === t.key));

  // ── Full-page search (all tabs) ────────────────────────────────────────────
  const stripHtml = (s) => (s || '').replace(/<[^>]*>/g, '');
  // Language keywords → the Language.md section that documents each (slugs come
  // from HELP_TOC). Lets `if`, `for`, `function`, … resolve to the language docs.
  const LANGUAGE_KEYWORDS = {
    if: 'conditionals', then: 'conditionals', else: 'conditionals', elseif: 'conditionals', endif: 'conditionals',
    while: 'while-loops', endwhile: 'while-loops',
    for: 'for-loops', to: 'for-loops', step: 'for-loops', next: 'for-loops',
    repeat: 'repeat-loops', until: 'repeat-loops',
    do: 'do-loops', loop: 'do-loops',
    exit: 'control-statements', skip: 'control-statements',
    select: 'select-statements', case: 'select-statements', endcase: 'select-statements', endselect: 'select-statements', default: 'select-statements',
    defer: 'defer-statements',
    function: 'functions', endfunction: 'functions', exitfunction: 'functions',
    global: 'scopes', local: 'scopes',
    dim: 'arrays',
    as: 'casting',
    type: 'user-defined-types', endtype: 'user-defined-types',
    goto: 'goto', gosub: 'gosub', return: 'gosub',
    and: 'operations', or: 'operations', not: 'operations', mod: 'operations',
    rem: 'comments', remstart: 'comments', remend: 'comments',
    constant: 'compile-time-constants',
    end: 'end', true: 'literals', false: 'literals',
  };
  // Primitive data types (classic BASIC names + C# equivalents) → the Primitive
  // Types section. Both spellings are valid in declarations, so both search.
  const PRIMITIVE_TYPES = [
    'integer', 'int', 'double integer', 'long', 'byte', 'word', 'ushort',
    'dword', 'uint', 'boolean', 'bool', 'float', 'double float', 'double', 'string',
  ];
  let searchQuery = $state('');
  let searchOpen = $state(false);
  let searchBlurTimer;      // pending "close on blur" timer, cancelled on refocus
  let searchIndex = null;   // built lazily (cmdSlug is defined further down)
  function buildSearchIndex() {
    if (searchIndex) return searchIndex;
    const out = [];
    for (const t of HELP_TOC) out.push({ tab: 'language', label: t.text, anchor: t.slug, kind: 'Language', sub: '' });
    for (const [kw, anchor] of Object.entries(LANGUAGE_KEYWORDS)) out.push({ tab: 'language', label: kw, anchor, kind: 'Keyword', sub: 'Language keyword' });
    for (const t of PRIMITIVE_TYPES) out.push({ tab: 'language', label: t, anchor: 'primitive-types', kind: 'Type', sub: 'Primitive data type' });
    for (const t of GAME_TOC) out.push({ tab: 'tutorial', label: t.text, anchor: t.slug, kind: 'Tutorial', sub: '' });
    const seen = new Set();
    for (const g of GAME_COMMAND_GROUPS) for (const c of g.commands) {
      if (seen.has(c.name)) continue; seen.add(c.name);
      out.push({ tab: 'commands', label: c.name, anchor: cmdSlug(c.name), kind: 'Command', sub: stripHtml(c.desc).slice(0, 100) });
    }
    return (searchIndex = out);
  }
  let searchResults = $derived.by(() => {
    const q = searchQuery.trim().toLowerCase();
    if (q.length < 2) return [];
    const scored = [];
    for (const e of buildSearchIndex()) {
      if (BETA_TABS.has(e.tab) && !betaUnlocked) continue;   // don't leak beta docs via search
      const label = e.label.toLowerCase();
      let score = -1;
      if (label === q) score = 0;
      else if (label.startsWith(q)) score = 1;
      else if (label.includes(q)) score = 2;
      else if (e.sub.toLowerCase().includes(q)) score = 3;
      if (score >= 0) scored.push({ ...e, score });
    }
    scored.sort((a, b) => a.score - b.score || a.label.length - b.label.length);
    return scored.slice(0, 12);
  });
  function gotoSearch(r) {
    if (!r) return;
    searchOpen = false; searchQuery = '';
    location.hash = routeFor(r.tab, r.anchor);
  }

  // The blocks the body renders as prose + try-it snippets. Language/Tutorial
  // come from their generated markdown; the Commands tab synthesizes blocks from
  // the selected command's examples, so all three share ONE snippet UI + morph.
  let blocks = $derived(
    tab === 'commands'
      ? (selectedCmd?.examples ?? []).map((ex) => ({ type: 'code', runnable: true, code: ex.code, caption: ex.caption }))
      : (DOCS[tab]?.blocks ?? [])
  );
  let currentRuntime = $derived(tab === 'language' ? '' : 'monogame');
  let currentToc = $derived(DOCS[tab]?.toc ?? []);

  // Format every snippet through the LSP document formatter at render time (the
  // engine owns the canonical style, so we don't bake it into the generated
  // data). Results are cached by original source; `viewBlocks` swaps in the
  // formatted code so display, the editor, the morph, and copy all agree.
  let formatted = $state({});
  const fmtRequested = new Set();
  $effect(() => {
    const bs = blocks;
    const mono = currentRuntime === 'monogame';
    for (const b of bs) {
      if (b.type !== 'code' || !b.code || fmtRequested.has(b.code)) continue;
      fmtRequested.add(b.code);
      formatFadeSource('/fade/', b.code, mono)
        .then((f) => { if (f && f !== b.code) formatted = { ...formatted, [b.code]: f }; })
        .catch(() => {});
    }
  });
  let viewBlocks = $derived(blocks.map((b) => (b.type === 'code' && formatted[b.code]) ? { ...b, code: formatted[b.code] } : b));

  // Scroll-spy: which section is currently in view (language/tutorial only).
  let activeSlug = $state(null);
  $effect(() => {
    if (tab === 'commands') return;
    const toc = currentToc;
    const heads = toc.map((t) => document.getElementById(t.slug)).filter(Boolean);
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

  // ── Commands tab: grouped command reference ───────────────────────────────
  const cmdSlug = (name) => name.toLowerCase().replace(/[^\w]+/g, '-').replace(/^-|-$/g, '');
  let gameExpanded = $state(new Set());        // expanded group names
  function toggleGroup(name) {
    const s = new Set(gameExpanded);
    s.has(name) ? s.delete(name) : s.add(name);
    gameExpanded = s;
  }
  // Pick a command (from a link or the anchor) and reset the live snippet.
  function selectCmd(c) { selectedCmd = c; active = null; coloredCode.clear(); }
  // Resolve the selected command from the route anchor and auto-expand its group.
  function syncCommands() {
    const { anchor } = parseHash();
    if (!anchor) { selectCmd(null); return; }
    for (const g of GAME_COMMAND_GROUPS) {
      const c = g.commands.find((cc) => cmdSlug(cc.name) === anchor);
      if (c) {
        selectCmd(c);
        if (!gameExpanded.has(g.name)) { const s = new Set(gameExpanded); s.add(g.name); gameExpanded = s; }
        return;
      }
    }
  }

  function scrollToAnchor(smooth, tries = 12) {
    const { anchor } = parseHash();
    if (!anchor) return;
    const el = document.getElementById(anchor);
    if (el) { el.scrollIntoView({ behavior: smooth ? 'smooth' : 'auto', block: 'start' }); activeSlug = anchor; return; }
    // The element may not exist yet if we just switched tabs (the new tab's body
    // hasn't rendered). Retry across a few frames until it appears.
    if (tries > 0) requestAnimationFrame(() => scrollToAnchor(smooth, tries - 1));
  }

  // Point the URL bar at a section (tab + in-page anchor) and copy the link.
  // Setting the hash fires hashchange, which scrolls us there.
  function linkToSection(id) {
    location.hash = routeFor(tab, id);
    navigator.clipboard?.writeText(location.href);
    showToast('Link copied');
  }

  let toast = $state('');
  let toastTimer;
  function showToast(msg) { toast = msg; clearTimeout(toastTimer); toastTimer = setTimeout(() => (toast = ''), 1400); }

  // TOC links carry the full hash, so clicking one updates the hash → hashchange
  // → scroll. Highlight immediately without waiting for spy.
  function goto(slug) { activeSlug = slug; }

  // Re-stamp the GitHub-style hover anchors onto whatever headings the current
  // tab rendered (language + tutorial prose). Called on mount and tab switch.
  async function stampHeadingAnchors() {
    await tick();
    for (const h of document.querySelectorAll('.help-body h1[id], .help-body h2[id], .help-body h3[id], .help-body h4[id]')) {
      if (h.querySelector('.heading-anchor')) continue;
      const a = document.createElement('a');
      a.className = 'heading-anchor';
      a.href = routeFor(tab, h.id);
      a.title = 'Copy link to this section';
      a.setAttribute('aria-label', 'Copy link to this section');
      a.textContent = '#';
      a.addEventListener('click', (e) => { e.preventDefault(); linkToSection(h.id); });
      h.appendChild(a);
    }
  }

  function syncRoute(smooth) {
    active = null;
    if (tab === 'commands') syncCommands();
    else { stampHeadingAnchors(); scrollToAnchor(smooth); }
  }

  onMount(() => {
    const { legacy } = parseHash();
    // Canonicalize a legacy #/help link to the new #/learn scheme without a
    // history entry, so back/forward stay clean.
    if (legacy) history.replaceState(null, '', routeFor(tab, parseHash().anchor));
    syncRoute(false);
    const onHash = () => { betaUnlocked = normalizeBeta(); tab = parseHash().tab; syncRoute(true); };
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
    const injected = hintLines(viewBlocks[i]).length;
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
    const injected = hintLines(viewBlocks[active]).length;
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
  // The print-nudge only makes sense for the web (Language) runtime — MonoGame
  // snippets are graphical, so they never get an auto-nudge (only explicit hints).
  const hintFor = (block) => block.hint ?? (currentRuntime === '' && !/\bprint\b/i.test(block.code) ? NUDGE : null);
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

<!-- Shared prose + try-it snippet body. Drives Language, Game Tutorial, and the
     Commands tab's per-command examples from the reactive `blocks` array, so all
     three get the identical layout, editor width, and markdown→editor morph. -->
{#snippet bodyBlocks()}
  {#each viewBlocks as block, i}
    {#if block.type === 'html'}
      {@html block.html}
    {:else}
      {#if block.caption}<p class="snip-caption">{@html block.caption}</p>{/if}
      <div class="snip">
        {#if block.runnable && active === i}
          <div class="snip-live" out:editorFade>
            <fade-runnable class="ide" layout="ide" debug closable hide-run
              runtime={currentRuntime || undefined}
              asset-base="/fade/" code={block.code}
              hint={hintLines(block).join('\n')} onfadeclose={(e) => closeIt(e)}></fade-runnable>
          </div>
        {:else if block.runnable}
          <div class="snip-static">
            {#if canTryIt}
              <button class="snip-btn try" onclick={(e) => openIt(i, e)}>
                <span class="codicon codicon-play"></span> try it
              </button>
            {/if}
            <button class="snip-btn copy" title="Copy code" aria-label="Copy code" onclick={() => copy(block.code, i)}>
              <span class="codicon codicon-{copied === i ? 'check' : 'copy'}"></span>
            </button>
            <fade-code runtime={currentRuntime || undefined} asset-base="/fade/" code={block.code} commands={block.commands?.join(",")}></fade-code>
          </div>
        {:else}
          <div class="snip-static">
            <button class="snip-btn copy" title="Copy code" aria-label="Copy code" onclick={() => copy(block.code, i)}>
              <span class="codicon codicon-{copied === i ? 'check' : 'copy'}"></span>
            </button>
            <fade-code runtime={currentRuntime || undefined} asset-base="/fade/" code={block.code} commands={block.commands?.join(",")}></fade-code>
          </div>
        {/if}
      </div>
    {/if}
  {/each}
{/snippet}

<nav class="help-tabs">
  <a class="help-tabs-home" href="#/" aria-label="Home">← Home</a>
  {#each visibleTabs as t}
    <a class="help-tab" class:active={tab === t.key} href="#{t.route}">{t.label}</a>
  {/each}
  <label class="help-theme" title="Theme">
    <select aria-label="Theme" value={theme} onchange={(e) => pickTheme(e.currentTarget.value)}>
      {#each THEMES as t}<option value={t.id}>{t.label}</option>{/each}
    </select>
  </label>
  <div class="help-search">
    <span class="codicon codicon-search"></span>
    <input
      type="text" placeholder="Search all docs…" aria-label="Search docs"
      bind:value={searchQuery}
      onfocus={() => { clearTimeout(searchBlurTimer); searchOpen = true; }}
      oninput={() => (searchOpen = true)}
      onblur={() => { searchBlurTimer = setTimeout(() => (searchOpen = false), 150); }}
      onkeydown={(e) => {
        if (e.key === 'Escape') { searchQuery = ''; searchOpen = false; e.currentTarget.blur(); }
        else if (e.key === 'Enter') gotoSearch(searchResults[0]);
      }} />
    {#if searchOpen && searchResults.length}
      <div class="help-search-results">
        {#each searchResults as r}
          <button class="help-search-item" onmousedown={(e) => e.preventDefault()} onclick={() => gotoSearch(r)}>
            <span class="help-search-kind help-search-kind--{r.tab}">{r.kind}</span>
            <span class="help-search-label">{r.label}</span>
            {#if r.sub}<span class="help-search-sub">{r.sub}</span>{/if}
          </button>
        {/each}
      </div>
    {/if}
  </div>
</nav>

<div class="help-layout" class:toc-drawer={tocDrawer} class:toc-open={tocOpen}>
  {#if BETA_TABS.has(tab)}
    <div class="beta-banner" role="status">
      <span class="beta-tag">BETA</span>
      This section is a work in progress — content and behavior may change.
    </div>
  {/if}
  {#if tocDrawer}
    <button class="toc-toggle" aria-label="Table of contents" onclick={() => (tocOpen = !tocOpen)}>
      <span class="codicon codicon-list-unordered"></span>
    </button>
    {#if tocOpen}<button class="toc-scrim" aria-label="Close" onclick={() => (tocOpen = false)}></button>{/if}
  {/if}
  <nav class="toc" onclick={() => { if (tocDrawer) tocOpen = false; }}>
    {#if tab === 'commands'}
      <div class="toc-title">Commands</div>
      <ul class="toc-list cmd-toc">
        {#each GAME_COMMAND_GROUPS as g}
          <li class="cmd-group">
            <button class="cmd-group-head" onclick={() => toggleGroup(g.name)}>
              <span class="codicon codicon-chevron-{gameExpanded.has(g.name) ? 'down' : 'right'}"></span>
              <span class="cmd-group-name">{g.name}</span>
              <span class="cmd-count">{g.commands.length}</span>
            </button>
            {#if gameExpanded.has(g.name)}
              <ul class="cmd-list">
                {#each g.commands as c}
                  <li>
                    <a href={routeFor('commands', cmdSlug(c.name))} class:active={selectedCmd?.name === c.name}
                       onclick={() => selectCmd(c)}>{c.name}</a>
                  </li>
                {/each}
              </ul>
            {/if}
          </li>
        {/each}
      </ul>
    {:else}
      <div class="toc-title">{DOCS[tab].title}</div>
      <ul class="toc-list">
        {#each currentToc as item}
          <li class="lvl-{item.level}">
            <a href={routeFor(tab, item.slug)} data-slug={item.slug} class:active={item.slug === activeSlug}
               onclick={() => goto(item.slug)}>{item.text}</a>
          </li>
        {/each}
      </ul>
    {/if}
  </nav>

  {#if tab === 'commands'}
    <article class="help-body">
      {#if selectedCmd}
        <div class="cmd-doc">
          <h1 class="cmd-name">{selectedCmd.name}</h1>
          {#if selectedCmd.desc}<p class="cmd-desc">{@html selectedCmd.desc}</p>{/if}
          <h2>Parameters</h2>
          {#if selectedCmd.params.length}
            {#each selectedCmd.params as p}
              <div class="cmd-param">
                <code class="cmd-type">{p.type}</code>
                <span class="cmd-pname">{p.name}</span>
                {#if p.modifier}<span class="cmd-mod">({p.modifier})</span>{/if}
                {#if p.desc}<span class="cmd-pdesc">— {@html p.desc}</span>{/if}
              </div>
            {/each}
          {:else}
            <p class="cmd-none">None</p>
          {/if}
          {#if selectedCmd.returns}
            <h2>Returns</h2>
            <div class="cmd-param"><code class="cmd-type">{selectedCmd.returns.type}</code>{#if selectedCmd.returns.desc}<span class="cmd-pdesc">— {@html selectedCmd.returns.desc}</span>{/if}</div>
          {/if}
          {#if selectedCmd.remarks}
            <h2>Remarks</h2>
            <p class="cmd-remarks">{@html selectedCmd.remarks}</p>
          {/if}
          {#if blocks.length}
            <h2>Examples</h2>
          {/if}
        </div>
        {@render bodyBlocks()}
      {:else}
        <div class="cmd-doc">
          <h1>Commands</h1>
          <p>The full command reference for the MonoGame runtime. Pick a category on the left and choose a command to see its parameters, return value, and runnable examples.</p>
          <p style="color:var(--fg-muted)">New to the game runtime? Start with the <a href="#/learn/tutorial" style="color:var(--accent)">Game Tutorial</a>.</p>
        </div>
      {/if}
    </article>
  {:else}
    <article class="help-body">
      {@render bodyBlocks()}
    </article>
  {/if}
  {#if toast}<div class="help-toast">{toast}</div>{/if}
</div>

<style>
  /* Sticky docs tabs across the very top (Language | Game Commands | …). */
  .help-tabs {
    position: fixed; top: 0; left: 0; right: 0; height: 40px; z-index: 30;
    display: flex; align-items: stretch; gap: 2px; padding: 0 10px;
    background: var(--bg); border-bottom: 1px solid var(--border-2);
  }
  .help-tabs-home { display: inline-flex; align-items: center; color: var(--fg-muted); text-decoration: none; font-size: 0.82rem; padding: 0 12px 0 4px; margin-right: 6px; border-right: 1px solid var(--border-2); }
  .help-tabs-home:hover { color: var(--fg); }
  .help-tab {
    display: inline-flex; align-items: center; padding: 0 16px; color: var(--fg-muted);
    text-decoration: none; font-size: 0.9rem; border-bottom: 2px solid transparent;
  }
  .help-tab:hover { color: var(--fg); }
  .help-tab.active { color: var(--fg); border-bottom-color: var(--accent); }

  /* Theme picker + search, right-aligned; picker sits to the left of search. */
  .help-search { position: relative; display: flex; align-items: center; gap: 6px; align-self: center; }
  .help-search > .codicon { color: var(--fg-muted); font-size: 14px; pointer-events: none; }
  .help-search input {
    height: 30px; box-sizing: border-box;
    background: var(--bg-3); border: 1px solid var(--border-2); border-radius: 6px; color: var(--fg);
    font: inherit; font-size: 0.82rem; padding: 0 8px; width: 220px;
  }
  .help-search input:focus { outline: none; border-color: var(--accent); }
  .help-search-results {
    position: absolute; top: calc(100% + 6px); right: 0; width: 400px; max-width: 80vw;
    max-height: 64vh; overflow-y: auto; background: var(--bg-2); border: 1px solid var(--border-2);
    border-radius: 8px; box-shadow: 0 10px 34px rgba(0, 0, 0, 0.55); z-index: 40; padding: 4px;
  }
  .help-search-item {
    display: flex; align-items: center; gap: 8px; width: 100%; text-align: left;
    background: none; border: 0; color: var(--fg); padding: 6px 8px; border-radius: 6px;
    cursor: pointer; font: inherit; font-size: 0.85rem;
  }
  .help-search-item:hover { background: var(--hover-bg); }
  .help-search-kind {
    flex: 0 0 auto; font-size: 0.6rem; text-transform: uppercase; letter-spacing: 0.04em;
    padding: 2px 6px; border-radius: 4px; background: var(--border-2); color: var(--fg-muted);
  }
  .help-search-kind--commands { background: #3a2f4a; color: #c586c0; }
  .help-search-kind--tutorial { background: #2a3a4a; color: #4aa3ff; }
  .help-search-kind--language { background: #2a4a3a; color: #5ac57a; }
  .help-search-label { flex: 0 0 auto; color: var(--fg); font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
  .help-search-sub { flex: 1 1 auto; color: var(--fg-muted); font-size: 0.78rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap; }

  /* Theme picker */
  .help-theme { display: flex; align-items: center; align-self: center; margin-left: auto; margin-right: 8px; }
  .help-theme select {
    height: 30px; box-sizing: border-box;
    background: var(--bg-3); border: 1px solid var(--border-2); border-radius: 6px; color: var(--fg);
    font: inherit; font-size: 0.8rem; padding: 0 6px; cursor: pointer;
  }
  .help-theme select:focus { outline: none; border-color: var(--accent); }

  .help-layout {
    padding-left: 240px;   /* reserve room for the fixed TOC */
    padding-top: 40px;     /* clear the fixed tabs bar */
    text-align: left;
  }

  /* Table of contents — fixed to the LEFT EDGE of the screen (not the centered
     content), so it stays put as the body scrolls. Sits below the tabs bar. */
  .toc {
    position: fixed;
    left: 0;
    top: 40px;
    width: 240px;
    height: calc(100vh - 40px);
    overflow-y: auto;
    box-sizing: border-box;
    padding: 1.25rem 0.75rem 2rem;
    border-right: 1px solid var(--border-2);
    font-size: 0.9rem;
    background: var(--bg-2);
    z-index: 5;
  }
  /* Slim pre-release notice; spans the content region below the tabs bar. */
  .beta-banner {
    display: flex; align-items: center; gap: 8px;
    padding: 7px 16px;
    font-size: 0.82rem;
    color: var(--fg-2);
    background: color-mix(in srgb, var(--accent) 14%, var(--bg-2));
    border-bottom: 1px solid color-mix(in srgb, var(--accent) 35%, var(--border-2));
  }
  .beta-tag {
    flex: 0 0 auto;
    font-weight: 700; font-size: 0.68rem; letter-spacing: 0.06em;
    padding: 2px 6px; border-radius: 4px;
    background: var(--accent); color: var(--on-accent);
  }
  .toc-title { font-weight: 700; margin: 0.25rem 0 0.5rem; color: var(--fg); }
  .toc-list { list-style: none; margin: 0; padding: 0; }
  .toc-list li { margin: 1px 0; }
  .toc-list a {
    display: block;
    color: var(--fg-muted);
    text-decoration: none;
    padding: 3px 6px;
    border-radius: 4px;
    border-left: 2px solid transparent;
  }
  .toc-list a:hover { color: var(--fg); background: var(--hover-bg); }
  /* Moving position indicator — the section currently in view. */
  .toc-list a { transition: border-left-color 0.15s, color 0.15s; }
  .toc-list a.active { border-left-color: var(--accent); color: var(--fg); font-weight: 600; }
  .toc-list .lvl-1 { font-weight: 600; margin-top: 0.4rem; }
  .toc-list .lvl-2 a { padding-left: 6px; }
  /* Nested subsections (the docs use #### under ##). */
  .toc-list .lvl-3 a,
  .toc-list .lvl-4 a { padding-left: 22px; font-size: 0.82rem; color: var(--fg-muted); }

  /* ── Game Commands: grouped TOC + doc panel ─────────────────────────── */
  .cmd-toc { }
  .cmd-group-head {
    width: 100%; display: flex; align-items: center; gap: 6px; background: none;
    border: 0; color: var(--fg-2); font: inherit; font-size: 0.9rem; cursor: pointer;
    padding: 4px 6px; border-radius: 4px; text-align: left;
  }
  .cmd-group-head:hover { background: var(--hover-bg); color: var(--fg); }
  .cmd-group-head .codicon { font-size: 14px; color: var(--fg-muted); flex: 0 0 auto; }
  .cmd-group-name { flex: 1 1 auto; }
  .cmd-count { flex: 0 0 auto; color: var(--fg-muted); font-size: 0.75rem; }
  .cmd-list { list-style: none; margin: 0 0 2px; padding: 0 0 2px; }
  .cmd-list a {
    display: block; color: var(--fg-muted); text-decoration: none; padding: 2px 6px 2px 26px;
    font-size: 0.82rem; border-radius: 4px; border-left: 2px solid transparent;
    white-space: nowrap; overflow: hidden; text-overflow: ellipsis;
  }
  .cmd-list a:hover { color: var(--fg); background: var(--hover-bg); }
  .cmd-list a.active { border-left-color: var(--accent); color: var(--fg); background: var(--list-active-bg); }

  .cmd-doc :global(h1), .cmd-name { font-size: 1.6rem; margin: 0 0 0.5rem; color: var(--link-fg); font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
  .cmd-doc h2 { font-size: 0.78rem; text-transform: uppercase; letter-spacing: 0.05em; color: var(--fg-muted); margin: 1.6rem 0 0.5rem; border-bottom: 1px solid var(--border-2); padding-bottom: 4px; }
  .cmd-desc { color: var(--fg); }
  .cmd-param { padding: 4px 0; color: var(--fg); }
  .cmd-type { background: var(--bg-3); color: var(--fg); padding: 1px 6px; border-radius: 4px; font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.85em; }
  .cmd-pname { color: var(--link-fg); font-weight: 600; margin-left: 6px; }
  .cmd-mod { color: var(--fg-muted); font-style: italic; margin-left: 4px; }
  .cmd-pdesc { color: var(--fg-muted); margin-left: 4px; }
  .cmd-none { color: var(--fg-muted); font-style: italic; }
  .cmd-remarks { color: var(--fg-2); white-space: pre-wrap; }
  /* Caption above a command example / tutorial snippet (from the docs prose). */
  .snip-caption { color: var(--fg-2); margin: 1.2rem 0 0.4rem; }
  .snip-caption :global(code) {
    background: var(--bg-3); color: #d7ba7d; padding: 1px 5px; border-radius: 3px;
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.85em;
  }
  /* Examples heading sits tighter above the first snippet than the doc h2s. */
  .cmd-doc h2:last-child { margin-bottom: 0; }
  /* Inline code inside doc prose (from {@html}) — distinct from the type chip. */
  .cmd-desc :global(code), .cmd-remarks :global(code), .cmd-pdesc :global(code) {
    background: var(--bg-3); color: #d7ba7d; padding: 1px 5px; border-radius: 3px;
    font-family: ui-monospace, SFMono-Regular, Menlo, monospace; font-size: 0.85em;
  }

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
    color: var(--fg-muted); font-weight: 400; text-decoration: none;
    padding-right: 0.35em; cursor: pointer;
  }
  :global(.help-body h1:hover .heading-anchor),
  :global(.help-body h2:hover .heading-anchor),
  :global(.help-body h3:hover .heading-anchor),
  :global(.help-body h4:hover .heading-anchor),
  :global(.help-body .heading-anchor:focus) { opacity: 1; }
  :global(.help-body .heading-anchor:hover) { color: var(--accent); }

  .help-toast {
    position: fixed; bottom: 22px; left: 50%; transform: translateX(-50%);
    background: var(--accent); color: var(--fg); padding: 7px 16px; border-radius: 6px;
    font-size: 0.85rem; z-index: 80; box-shadow: 0 4px 16px rgba(0,0,0,0.4);
    animation: help-toast-in 0.16s ease;
  }
  @keyframes help-toast-in { from { opacity: 0; transform: translate(-50%, 6px); } to { opacity: 1; transform: translate(-50%, 0); } }
  .help-body :global(pre) { background: var(--code-bg); color: var(--fg); padding: 0.75rem; border-radius: 6px; border: 1px solid var(--border-2); overflow-x: auto; }
  .help-body :global(code) { font-family: ui-monospace, SFMono-Regular, Menlo, monospace; }
  .help-body :global(table) { border-collapse: collapse; }
  .help-body :global(td), .help-body :global(th) { border: 1px solid var(--border-2); padding: 4px 8px; }
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
  .help-body :global(.fade-callout--note)      { border-left-color: var(--accent); }
  .help-body :global(.fade-callout--note .fade-callout__title)      { color: var(--accent); }
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
    border: 1px solid var(--border-2);
    background: var(--bg-3);
    color: var(--fg-2);
    border-radius: 4px;
    padding: 4px 9px;
    cursor: pointer;
  }
  .snip-btn:hover { background: var(--btn-hover-bg); color: var(--fg); }
  .snip-btn .codicon { font-size: 13px; }
  .snip-static .try, .snip-static .copy { position: absolute; top: 9px; z-index: 2; }
  .snip-static .try { left: 8px; background: var(--accent); border-color: var(--accent); color: var(--on-accent); }
  .snip-static .try:hover { color: var(--on-accent); }
  .snip-static .try:hover { background: var(--accent-hover); }
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
    border: 1px solid var(--border-2);
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
    position: fixed; top: 48px; left: 10px; z-index: 25;
    width: 38px; height: 38px; display: inline-flex; align-items: center; justify-content: center;
    background: var(--accent); color: var(--fg); border: 0; border-radius: 8px; cursor: pointer;
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

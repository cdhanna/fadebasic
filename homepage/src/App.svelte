<script>
  import fadeLogo from "./../../images/ghost_lee.png";
  import fadeScreen from "./../../images/fade_screenshot2.png";
  import Help from "./Help.svelte";
  import { cubicInOut } from "svelte/easing";
  import "@fadebasic/components";

  // Horizontal page slide. `x` is the off-screen side as a percentage of the
  // page width (responsive, no innerWidth read). Svelte drives `u` 1→0 on
  // enter and 0→1 on leave, so a page flies in from / out to its own side.
  // The homepage sits on the LEFT (x:-100), the docs on the RIGHT (x:100), so
  // forward navigation slides left and Back slides right — direction is implicit
  // in each page's fixed side, no need to track nav direction.
  const slideX = (node, { x = 100, duration = 340 } = {}) => ({
    duration,
    easing: cubicInOut,
    css: (t, u) => `transform: translateX(${u * x}%)`,
  });

  // Slide the homepage off-screen before following an EXTERNAL link (the docs
  // slide is Svelte-transition-driven; a cross-origin nav can't animate the
  // incoming site, so we animate the exit then navigate). `dir` is the side the
  // page flies out to. Modified clicks (⌘/ctrl/shift = new tab) and
  // reduced-motion users bypass the animation.
  let leaving = $state("");
  let entering = $state("");
  const prefersReducedMotion =
    typeof window !== "undefined" && window.matchMedia("(prefers-reduced-motion: reduce)").matches;
  /**
   * @param {string} url
   * @param {"left" | "right"} [dir]
   */
  const slideAway = (url, dir = "left") => (/** @type {MouseEvent} */ e) => {
    if (e.metaKey || e.ctrlKey || e.shiftKey) return; // preserve open-in-new-tab
    e.preventDefault();
    if (prefersReducedMotion) { window.location.href = url; return; }
    if (leaving) return;
    leaving = dir;
    window.setTimeout(() => { window.location.href = url; }, 380);
  };

  // Browser back/forward: we leave the homepage by sliding LEFT, so returning to
  // it should slide back IN from the left. This also fixes the bfcache case —
  // without it, a restored page keeps its slid-out `leaving` state and shows
  // blank. Handles both bfcache restores (pageshow persisted) and back_forward
  // reloads. (Forward INTO the Playground can't re-trigger its slide: the
  // ?fadeEnter param was stripped from history — a documented cross-origin gap.)
  const playEnter = () => {
    leaving = ""; // clear any leftover exit state
    if (prefersReducedMotion) return;
    entering = "left";
    window.setTimeout(() => { entering = ""; }, 440);
  };
  if (typeof window !== "undefined") {
    const nav = /** @type {PerformanceNavigationTiming | undefined} */ (
      performance.getEntriesByType("navigation")[0]
    );
    if (nav?.type === "back_forward") playEnter();
    window.addEventListener("pageshow", (e) => { if (e.persisted) playEnter(); });
  }

  // "Try Online" target. On localhost, point at the local Playground dev server
  // (https://localhost:5311) so the exit→enter slide can be tested end-to-end;
  // in production it's the deployed Playground. `?fadeEnter=slide` tells the
  // Playground to slide itself in from the right on arrival (matches the exit).
  const isLocal =
    typeof location !== "undefined" &&
    (location.hostname === "localhost" || location.hostname === "127.0.0.1");
  const playgroundUrl =
    (isLocal ? "https://localhost:5311" : "https://dev.fadebasic.com") + "/?fadeEnter=slide";

  // The Try Online button's hover polish is pure CSS (soft fill + lift + a single
  // subtle sheen) — see the styles below. No JS action needed.

  // On phones the live IDE emulator (monaco + a WASM runtime) is too heavy and
  // too cramped — fall back to the original screenshot there. Checking a media
  // query lets us skip mounting the emulator entirely on mobile.
  let isMobile = $state(false);
  if (typeof window !== "undefined") {
    const mq = window.matchMedia("(max-width: 900px)");
    isMobile = mq.matches;
    mq.addEventListener("change", (e) => (isMobile = e.matches));
  }

  // FizzBuzz via a FUNCTION + SELECT/CASE — shows off the debugger (call stack
  // with two frames, locals per frame, a watch expression).
  const fizzbuzz = `REMSTART
 The first job I ever had, the interviewer asked me to implement FizzBuzz.
 I'm sure this is not what they meant.
REMEND

FOR t = 1 TO 30
    fizzBuzz(t)
NEXT

FUNCTION fizzBuzz(n)
    shouldFizz = (n MOD 3) = 0 \`1 when n is divisible by 3
    shouldBuzz = (n MOD 5) = 0 \`1 when n is divisible by 5

    SELECT(shouldFizz + shouldBuzz * 2)
        CASE 0
            PRINT n
        ENDCASE
        CASE 1
            PRINT "fizz"
        ENDCASE
        CASE 2
            PRINT "buzz"
        ENDCASE
        CASE 3 \`1 + 2
            PRINT "fizzbuzz"
        ENDCASE
    ENDSELECT
ENDFUNCTION`;

  // Hash router — GitHub Pages serves a single static index (200, no rewrite,
  // no 404-status fallback). The fragment carries the route as a path:
  // `#/learn/<tab>/<anchor>` (e.g. `#/learn/commands/sync`). The tab is part of
  // the path and the trailing segment is the in-page anchor — Help scrolls to it
  // with JS (no second `#`). Legacy `#/help*` links still resolve (Help redirects).
  let route = $state(location.hash);
  const onHash = () => { route = location.hash; };
  let showHelp = $derived(route.startsWith("#/learn") || route.startsWith("#/help"));
</script>

<svelte:window on:hashchange={onHash} />

<!-- Crossfade between the homepage and the docs. Both pages live in the SAME
     grid cell (grid-area 1/1), so during the transition they overlap instead of
     stacking — no layout jump. Opacity on the .page wrapper also fades Help's
     position:fixed chrome (tabs/TOC), since opacity applies to fixed
     descendants. -->
<div class="app-shell">
{#if showHelp}
  <div class="page" transition:slideX={{ x: 100 }}><Help /></div>
{:else}
  <div class="page" class:leaving-left={leaving === "left"} class:leaving-right={leaving === "right"} class:entering-left={entering === "left"} transition:slideX={{ x: -100 }}>
<main class="hero">
  <div class="hero-intro">
    <div class="content">
      <img src={fadeLogo} class="logo" alt="Fade Basic Logo" title="Ghost Lee" />
      <div>
        <h1 style="margin-bottom: 0px;">FADE BASIC</h1>
        <p><i><b>F</b>ade's <b>A</b>ctually <b>D</b>otnet <b>E</b>mbeddable</i></p>
      </div>
    </div>

    <div class="buttons" style="display: flex; flex-direction: row; justify-content: space-between;">
      <a href={playgroundUrl} onclick={slideAway(playgroundUrl, "left")}><button class="try-online"><span class="try-label">Try Online</span></button></a>
      <a href="#/learn/language"><button>Learn</button></a>
      <a href="https://discord.gg/yxFAFJurvU" unselectable="off"><button>Discord</button></a>
    </div>

    <div style="text-align: left;">
      <p><i>Fade</i> is a <b>BASIC</b>-<i>esque</i> scripting language for Dotnet that,</p>
      <ul>
        <li>Is debuggable,</li>
        <li>Uses no runtime Reflection,</li>
        <li>Has minimal dependencies,</li>
        <li>Has a test framework,</li>
        <li>Has a compile time macro system,</li>
        <li>Runs in dotnet, or in WASM,</li>
        <li>Is Customizable.</li>
      </ul>
      <p> <i>Fade</i> is <a href="https://github.com/cdhanna/fadebasic/tree/main?tab=readme-ov-file#fade-basic" onclick={slideAway("https://github.com/cdhanna/fadebasic/tree/main?tab=readme-ov-file#fade-basic", "left")}>open source</a> and created by <a href="https://brewed.ink">Chris Hanna</a> </p>
    </div>
  </div>

  <section class="tryit">
    {#if isMobile}
      <a class="shot" href={fadeScreen} target="_blank">
        <img src={fadeScreen} alt="Screenshot of Fade in VSCode" title="It works in Visual Studio Code!" />
      </a>
    {:else}
      <fade-runnable
        class="ide"
        layout="ide"
        debug
        hide-run
        theme-picker
        breakpoints="25"
        asset-base="/fade/"
        code={fizzbuzz}
      ></fade-runnable>
    {/if}
  </section>
</main>
  </div>
{/if}
</div>

<style>
  /* Overlapping pages share one grid cell so the slide doesn't reflow; clip the
     off-screen page horizontally so it can't spawn a scrollbar mid-transition. */
  .app-shell { display: grid; width: 100%; min-width: 0; overflow-x: clip; }
  .app-shell > .page { grid-area: 1 / 1; min-width: 0; }

  /* External-nav exit: slide the homepage off-screen, then the click handler
     navigates (see slideAway). A keyframe animation (not a transition) so it
     runs from the class add without a prior state and never fights the Svelte
     slideX transition used for in-app docs navigation. */
  @keyframes hp-slide-away-left  { to { transform: translateX(-100%); opacity: 0; } }
  @keyframes hp-slide-away-right { to { transform: translateX(100%);  opacity: 0; } }
  .page.leaving-left  { animation: hp-slide-away-left  380ms cubic-bezier(0.4, 0, 0.2, 1) forwards; }
  .page.leaving-right { animation: hp-slide-away-right 380ms cubic-bezier(0.4, 0, 0.2, 1) forwards; }
  /* Return via browser back/forward: slide back in from the left (mirrors the
     exit). Also overrides any restored leaving state so bfcache never shows the
     page slid-out. */
  @keyframes hp-slide-in-left { from { transform: translateX(-100%); opacity: 0; } to { transform: translateX(0); opacity: 1; } }
  .page.entering-left { animation: hp-slide-in-left 420ms cubic-bezier(0.22, 1, 0.36, 1) both; }

  /* "Try Online" — the primary CTA. Restrained hover polish only: the accent
     wash deepens, the button lifts a hair with a soft glow, and a single quiet
     specular sheen glides across once. No mini-animation, nothing busy. */
  .buttons button.try-online {
    background: color-mix(in srgb, var(--accent) 8%, transparent);
    color: var(--accent);
    border: 2px solid var(--accent);
    font-weight: 700;
    position: relative;
    overflow: hidden;         /* clip the sheen to the button shape */
    transition:
      background 0.22s ease,
      color 0.2s ease,
      border-color 0.2s ease,
      box-shadow 0.28s ease,
      transform 0.18s ease;
  }
  .buttons button.try-online:hover {
    background: color-mix(in srgb, var(--accent) 16%, transparent);
    border-color: var(--accent-hover);
    transform: translateY(-1px);
    box-shadow: 0 6px 18px color-mix(in srgb, var(--accent) 22%, transparent);
  }
  .buttons button.try-online:active { transform: translateY(0); }
  /* A single soft specular sheen that glides across once per hover. */
  .buttons button.try-online::before {
    content: "";
    position: absolute;
    inset: 0;
    pointer-events: none;
    background: linear-gradient(
      115deg,
      transparent 32%,
      color-mix(in srgb, #fff 55%, transparent) 50%,
      transparent 68%
    );
    transform: translateX(-150%);
    opacity: 0;
  }
  .buttons button.try-online:hover::before { animation: try-sheen 0.7s ease; }
  @keyframes try-sheen {
    0% { transform: translateX(-150%); opacity: 0; }
    18% { opacity: 0.5; }
    100% { transform: translateX(150%); opacity: 0; }
  }
  .buttons button.try-online .try-label { position: relative; z-index: 1; }
  @media (prefers-reduced-motion: reduce) {
    .page.leaving-left, .page.leaving-right, .page.entering-left { animation: none; }
    .buttons button.try-online { transition: background 0.2s ease, border-color 0.2s ease; }
    .buttons button.try-online:hover { transform: none; }
    .buttons button.try-online:hover::before { animation: none; }
  }

  /* Two-column hero: intro copy on the left, the live IDE emulator on the
     right (where the screenshot used to sit). Wraps to a single column on
     narrow screens. */
  /* body is `display:flex` (Vite template) and the app mounts into #app, which
     as a flex item shrink-wraps to its content — capping the hero (and the
     emulator) well short of the viewport. Make #app fill the width so the hero
     can use it. */
  :global(#app) { width: 100%; min-width: 0; }

  .hero {
    display: flex;
    flex-wrap: wrap;
    gap: 2rem;
    align-items: flex-start;
    justify-content: center;
    max-width: min(96vw, 2100px);
    margin: 0 auto;
    /* padding: 1rem; */
    box-sizing: border-box;
  }
  .hero-intro { flex: 0 0 460px; min-width: 320px; }
  /* Keep the display title on one line. */
  .hero-intro h1 { white-space: nowrap; }
  /* Low flex-basis (not 860): flexbox decides wrapping from the basis, not the
     shrunk size, so a big basis wraps the emulator to its own row too eagerly.
     A small basis + flex-grow:1 keeps it beside the intro and filling the
     remaining width down to the mobile breakpoint. */
  .tryit { flex: 1 1 340px; min-width: 640px; text-align: left; margin: 0; }
  .tryit > p { margin-top: 0; }
  /* Don't set `display` here — the component owns it (grid in IDE mode). */
  .tryit :global(fade-runnable.ide) { margin-top: 0.75rem; height: min(95vh, 960px); box-shadow: 0 8px 30px rgba(0,0,0,0.35); }
  .tryit .shot img { width: 100%; height: auto; border-radius: 8px; box-shadow: 0 8px 30px rgba(0,0,0,0.35); }
</style>

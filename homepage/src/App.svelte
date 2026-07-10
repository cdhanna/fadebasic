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
  <div class="page" transition:slideX={{ x: -100 }}>
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
      <a href="https://github.com/cdhanna/fadebasic/tree/main?tab=readme-ov-file#fade-basic"><button>Install</button></a>
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
        <li>Compiles to WASM,</li>
        <li>Is Customizable.</li>
      </ul>
      <p> <i>Fade</i> is open source and created by <a href="https://brewed.ink">Chris Hanna</a> </p>
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

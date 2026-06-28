// @vitest-environment jsdom

// Regression test for the relayed-observer Debug UI render bug.
// Reproduces, without a live session, the exact envelope shape the
// observer receives over the wire when the host runs:
//
//     begin debug window "shaders"
//         if (debug float slider("Glitch Amount", glitchAmount#))
//             ...
//         endif
//     end debug window
//
// On the live session, the observer logs confirm the envelope arrives,
// applyFrameEnvelope is called without throwing, and the DOM contains
// `<div class="tp-rotv_t">shaders</div>` — but the user reports no
// slider visible. This test drives the same envelope through
// mountDebugUiPanel + applyFrameEnvelope and asserts that the
// FLOAT_SLIDER binding row + slider widget actually make it into the
// DOM.

import { describe, it, expect, beforeEach } from 'vitest';
import { mountDebugUiPanel } from './debug-ui-panel';
import type { DebugUiCommand, DebugUiFrameEnvelope } from './monogame-host';

let container: HTMLElement;

beforeEach(() => {
    document.body.innerHTML = '';
    container = document.createElement('div');
    // Give the container the same height context dockview gives the
    // real panel — the panel's mount sets `height: 100%` and Tweakpane
    // measures the parent for layout. Without a sized parent jsdom
    // happily reports 0×0 and some bindings short-circuit their render.
    container.style.width = '420px';
    container.style.height = '600px';
    document.body.appendChild(container);
});

// Command type codes — must match CT in debug-ui-panel.ts.
const T = {
    WINDOW_START: 0, WINDOW_END: 1,
    FLOAT_SLIDER: 15,
    ARG_FLOAT: 22,
} as const;

// No-op stubs for opts callbacks the panel doesn't need for this case
// (the slider only round-trips through sendFbasicChange on user input,
// not during render).
function makeOpts() {
    return {
        container,
        getSchema: async () => null,
        listEntities: async () => [],
        getEntity: async () => null,
        setField: async () => true,
        sendFbasicChange: () => { /* not exercised */ },
    };
}

/** Build the envelope the observer would receive for `begin debug
 *  window "shaders" / debug float slider("Glitch Amount", v#)`. The
 *  exact shape mirrors the wire bytes observed in the live session
 *  diagnostic logs. */
function shadersEnvelope(gen: number, value: number): DebugUiFrameEnvelope {
    const queue: DebugUiCommand[] = [
        { id: 88660769, t: T.WINDOW_START, l: 'shaders', s: null, i: 0, f: 0 },
        { id: 2143514761, t: T.FLOAT_SLIDER, l: 'Glitch Amount', s: null, i: 0, f: value },
        { id: -1369638928, t: T.ARG_FLOAT, l: null, s: null, i: 0, f: 0 },
        { id: -1369638928, t: T.ARG_FLOAT, l: null, s: null, i: 0, f: 100 },
        { id: 88660769, t: T.WINDOW_END, l: null, s: null, i: 0, f: 0 },
    ];
    return { gen, queue, autoInspector: false };
}

describe('debug-ui-panel relayed render', () => {
    it('initial empty envelope leaves the idle hint in place', () => {
        const h = mountDebugUiPanel(makeOpts());
        h.applyFrameEnvelope({ gen: 0, queue: [], autoInspector: false });
        // Idle hint is in the DOM until something fills the queue.
        expect(container.textContent ?? '').toContain('Run your program');
    });

    it('renders the shaders window AND the Glitch Amount slider row', () => {
        const h = mountDebugUiPanel(makeOpts());
        // Mirror the observer's actual sequence: one empty envelope at
        // gen=0 (the iframe's idle state before Run), then a non-empty
        // one at gen=1 (program loaded). The gen transition triggers
        // wipeAllProgramState; the second apply should rebuild from
        // scratch with the slider.
        h.applyFrameEnvelope({ gen: 0, queue: [], autoInspector: false });
        h.applyFrameEnvelope(shadersEnvelope(1, 25));

        const titleEls = container.querySelectorAll('.tp-rotv_t');
        const titles = Array.from(titleEls).map((el) => el.textContent);
        expect(titles).toContain('shaders');

        // The window title rendering was confirmed in production logs;
        // this is the bit the user can't see. Tweakpane renders each
        // binding row as `.tp-lblv` (label + value layout); the slider
        // itself is `.tp-sldv`.
        const bindings = container.querySelectorAll('.tp-lblv');
        const sliders = container.querySelectorAll('.tp-sldv');
        // Helpful error context: dump what DID land so a failure shows
        // exactly which classes are present.
        const dom = container.innerHTML.replace(/\s+/g, ' ').slice(0, 600);
        expect(bindings.length, `binding rows not rendered — DOM: ${dom}`).toBeGreaterThanOrEqual(1);
        expect(sliders.length, `slider widget not rendered — DOM: ${dom}`).toBeGreaterThanOrEqual(1);

        // Sanity: the slider's label should be the fbasic-side name.
        const labelEls = container.querySelectorAll('.tp-lblv_l');
        const labels = Array.from(labelEls).map((el) => el.textContent?.trim());
        expect(labels).toContain('Glitch Amount');
    });

    it('survives the gen transition that wipes prior state', () => {
        // First apply at gen=0 (no program) sets lastGen=0. Second
        // apply at gen=1 with queue MUST not lose the slider — that's
        // the path the observer hits when joining before the host has
        // started running.
        const h = mountDebugUiPanel(makeOpts());
        h.applyFrameEnvelope({ gen: 0, queue: [], autoInspector: false });
        h.applyFrameEnvelope({ gen: 0, queue: [], autoInspector: false });
        h.applyFrameEnvelope({ gen: 0, queue: [], autoInspector: false });
        // Program just loaded: gen bumps, queue arrives.
        h.applyFrameEnvelope(shadersEnvelope(1, 0));
        expect(container.querySelectorAll('.tp-sldv').length).toBeGreaterThanOrEqual(1);
    });

    it('subsequent frames with the same struct hash do not unmount the slider', () => {
        const h = mountDebugUiPanel(makeOpts());
        h.applyFrameEnvelope(shadersEnvelope(1, 0));
        const sliderCountAfterFirst = container.querySelectorAll('.tp-sldv').length;
        expect(sliderCountAfterFirst).toBeGreaterThanOrEqual(1);
        // Many subsequent frames at 60 fps — same shape, only value
        // changes. refreshWindow path; should NOT churn the DOM.
        for (let i = 1; i < 30; i++) {
            h.applyFrameEnvelope(shadersEnvelope(1, i));
        }
        expect(container.querySelectorAll('.tp-sldv').length).toBe(sliderCountAfterFirst);
    });
});

/**
 * Smoke probe for the live generation strip (.ai-genbar). Confirms the chat
 * pane mounts without error and the strip + its sub-elements exist and start
 * hidden. Behavioural token counting is exercised by the agent event loop at
 * runtime; this guards the markup/selector wiring that mountAiChat asserts on.
 *
 * Requires the Playground dev server (https://localhost:5311).
 */
import { chromium } from 'playwright';

const errors = [];
const browser = await chromium.launch({ headless: true });
const ctx = await browser.newContext({ ignoreHTTPSErrors: true });
const page = await ctx.newPage();
page.on('pageerror', e => errors.push('[PE] ' + e.message));
page.on('console', m => { if (m.type() === 'error') errors.push('[err] ' + m.text()); });

let verdict = null;
try {
    await page.goto('https://localhost:5311/', { waitUntil: 'domcontentloaded' });
    // Give the chat pane a moment to mount.
    await page.waitForSelector('.ai-genbar', { state: 'attached', timeout: 15000 });

    const shape = await page.evaluate(() => {
        const bar = document.querySelector('.ai-genbar');
        return {
            bar: !!bar,
            hidden: bar?.hasAttribute('hidden') ?? null,
            dot: !!document.querySelector('.ai-genbar-dot'),
            label: !!document.querySelector('.ai-genbar-label'),
            stats: !!document.querySelector('.ai-genbar-stats'),
        };
    });
    console.log('genbar shape:', JSON.stringify(shape));

    const mountErrors = errors.filter(e => /ai-genbar|Cannot read|querySelector|null/.test(e));
    if (!shape.bar || !shape.dot || !shape.label || !shape.stats) {
        verdict = 'genbar markup incomplete';
    } else if (shape.hidden !== true) {
        verdict = 'genbar should start hidden';
    } else if (mountErrors.length) {
        verdict = 'mount errors: ' + mountErrors.join(' | ');
    }
} catch (e) {
    verdict = e.message;
} finally {
    if (verdict) {
        console.error('\nconsole/page errors:\n' + errors.join('\n'));
        console.error(`\n── VERDICT ──\nFAIL: ${verdict}\n`);
    } else {
        console.log('\n── VERDICT ──\nPASS: .ai-genbar mounts, sub-elements present, starts hidden\n');
    }
    await browser.close();
    process.exit(verdict ? 1 : 0);
}

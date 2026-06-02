// Validate every stock-MonoGame shader fixture's transformed GLSL via
// real glslang (Khronos's official SPIR-V validator/compiler). Reads
// the snapshot vitest produced and runs glslang against each entry.
//
// This file is .cjs (CommonJS) on purpose. The @webgpu/glslang
// Emscripten module hangs on async/await + ESM dynamic import. CommonJS
// require + .then() callbacks work fine. Don't convert to .mjs/.js.

const fs = require('node:fs');
const path = require('node:path');

const SNAPSHOT_PATH = path.resolve(__dirname, '../src/shader/__fixtures__/stock-monogame-glsl-snapshot.json');
const entries = JSON.parse(fs.readFileSync(SNAPSHOT_PATH, 'utf8'));
console.log(`Loaded snapshot with ${entries.length} fixture(s)\n`);

const factory = require('@webgpu/glslang/dist/node-devel/glslang.js');

// Emscripten module is "thenable" — not a true Promise. Use .then callback
// (works) instead of await (hangs forever for unknown V8 reasons).
factory().then((glslang) => {
    console.log(`glslang loaded (compileGLSL: ${typeof glslang.compileGLSL})\n`);

    let failed = 0;
    let passed = 0;

    for (const fx of entries) {
        process.stdout.write(`[${fx.name.padEnd(14)}] `);

        // Hijack console.warn to capture glslang's printErr stream.
        const captured = [];
        const origWarn = console.warn;
        console.warn = (...args) => {
            captured.push(args.map((a) => (typeof a === 'string' ? a : String(a))).join(' '));
        };

        let success = false;
        let throwMsg = '';
        try {
            glslang.compileGLSL(fx.validationGlsl, 'fragment');
            success = true;
        } catch (e) {
            throwMsg = String(e && e.message ? e.message : e);
        } finally {
            console.warn = origWarn;
        }

        const errorLines = captured.filter((l) => /ERROR:/.test(l));
        if (success && errorLines.length === 0) {
            console.log('OK');
            passed++;
        } else {
            console.log('FAIL');
            failed++;
            console.log('  --- glslang error log ---');
            for (const line of captured) console.log('    ' + line);
            if (throwMsg) console.log('    (thrown: ' + throwMsg + ')');
            console.log('  --- transformed GLSL ---');
            fx.validationGlsl.split('\n').forEach((l, i) => {
                console.log(`    ${String(i + 1).padStart(3)} | ${l}`);
            });
            console.log('  --- end ---\n');
        }
    }

    console.log(`\n${passed}/${passed + failed} fixtures passed`);
    process.exit(failed === 0 ? 0 : 1);
});

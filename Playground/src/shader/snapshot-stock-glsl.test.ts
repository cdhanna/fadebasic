// Generates a JSON snapshot of every stock-MonoGame fixture's transformed
// GLSL (post FX-parse → HLSL translate → ES 3.10 validation transform).
// The plain-Node validation CLI (scripts/validate-stock-shaders.mjs) then
// reads this snapshot and feeds each entry to real glslang for SPIR-V
// validation.
//
// Why split it this way:
//   - vitest hangs when loading the @webgpu/glslang Emscripten module
//     (its synchronous WASM init incompatible with the worker fork).
//   - tsx (the only way to import .ts directly from a plain .mjs) also
//     hangs on the same glslang load.
//   - But vitest WITHOUT glslang is fine — it can run the TS pipeline
//     and write its output to a JSON.
//   - And plain Node WITHOUT tsx is fine — it can load glslang and read
//     JSON.
//   - So: vitest produces the snapshot, plain Node consumes it.
//
// The snapshot doubles as a regression-detection mechanism: editing the
// translator changes the snapshot, which shows up in git diff. Reviewers
// can see exactly which fixture's output changed and how.

import { describe, it, expect } from 'vitest';
import { writeFileSync } from 'node:fs';
import { resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';
import { parseFx } from './fx-parser';
import { translateHlslToGlsl } from './hlsl-translator';
import { transformEs100ToEs310ForValidation } from './glsl-validator';
import { ALL_STOCK_FIXTURES } from './__fixtures__/stock-monogame-shaders';

const __dirname = dirname(fileURLToPath(import.meta.url));
const SNAPSHOT_PATH = resolve(__dirname, '__fixtures__', 'stock-monogame-glsl-snapshot.json');

interface SnapshotEntry {
    name: string;
    entry: string;
    technique: string;
    /** GLSL ES 1.00 — what the translator emits and KNI consumes at runtime. */
    runtimeGlsl: string;
    /** GLSL ES 3.10 — what the validator feeds glslang for SPIR-V validation. */
    validationGlsl: string;
    /** Number of header lines the validator prepended (for line-offset math). */
    addedHeaderLines: number;
}

describe('Stock MonoGame shaders — snapshot generation', () => {
    it('generates a JSON snapshot for plain-Node glslang validation', () => {
        const entries: SnapshotEntry[] = [];
        for (const fixture of ALL_STOCK_FIXTURES) {
            const fx = parseFx(fixture.source);
            const translated = translateHlslToGlsl({
                source: fx.hlslStripped,
                entrypoint: fixture.entry,
                stage: 'pixel',
                cbuffers: fx.cbuffers,
                samplerStateLiterals: fx.samplerStateLiterals,
            });
            const { source: validationSource, addedLines } =
                transformEs100ToEs310ForValidation(translated.glsl, 'pixel');
            entries.push({
                name: fixture.name,
                entry: fixture.entry,
                technique: fixture.technique,
                runtimeGlsl: translated.glsl,
                validationGlsl: validationSource,
                addedHeaderLines: addedLines,
            });
        }
        writeFileSync(SNAPSHOT_PATH, JSON.stringify(entries, null, 2) + '\n', 'utf8');
        expect(entries.length).toBe(ALL_STOCK_FIXTURES.length);
        // Spot-check the SpriteEffect entry has the expected shape.
        const sprite = entries.find((e) => e.name === 'SpriteEffect');
        expect(sprite).toBeDefined();
        expect(sprite!.validationGlsl).toMatch(/^#version 310 es/);
        expect(sprite!.runtimeGlsl).not.toMatch(/#version/);   // ES 1.00 default
    });
});

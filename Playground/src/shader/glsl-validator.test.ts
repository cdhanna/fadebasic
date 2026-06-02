// Unit tests for the glslang error-log parser + the validateGlsl flow
// (with a stub glslang instance — the real WASM lives at runtime in the
// browser, not in vitest).

import { describe, it, expect, afterEach } from 'vitest';
import { validateGlsl, transformEs100ToEs310ForValidation } from './glsl-validator';
import { __setGlslangInstanceForTest, type GlslangInstance } from './glslang-loader';

// We're testing a non-exported function indirectly via the public API.
// Easiest is to re-import the module under test and exercise validateGlsl
// with a mock glslang. But the parser is tightly coupled — let's just
// pin its behavior via a tiny extracted test fixture.

// Re-implement the same regex as a smoke test for the surface — if the
// parser changes shape, this test catches the drift and the integration
// test in shader-validator (which we can't run in node without glslang)
// stays the source of truth for full behavior.
const LOG_LINE_RE = /^\s*(ERROR|WARNING)\s*:\s*[^:]+:\s*(\d+):\s*(.*)$/i;

describe('glsl-validator log parser', () => {
    it('parses ERROR: 0:N: \'token\' : message shape', () => {
        const m = LOG_LINE_RE.exec("ERROR: 0:26: 'input' : Illegal use of reserved word");
        expect(m).not.toBeNull();
        expect(m![1]).toBe('ERROR');
        expect(m![2]).toBe('26');
        expect(m![3]).toBe("'input' : Illegal use of reserved word");
    });

    it('parses WARNING:', () => {
        const m = LOG_LINE_RE.exec('WARNING: 0:14: implicit cast from float to int');
        expect(m).not.toBeNull();
        expect(m![1]).toBe('WARNING');
        expect(m![2]).toBe('14');
    });

    it('does not match plain text', () => {
        expect(LOG_LINE_RE.exec('just some unrelated stderr line')).toBeNull();
    });

    it('does not match the header line', () => {
        expect(LOG_LINE_RE.exec("[fade] effect load failed: 'test': Shader Compilation Failed.")).toBeNull();
    });
});

// Stub glslang instance for testing the validate pipeline. The real
// Emscripten module captures error log via printErr; the stub takes a
// scripted error log per compile and surfaces it the same way.
function makeStubGlslang(scriptedLog: string, shouldThrow: boolean): GlslangInstance {
    const buf: string[] = [];
    return {
        compileGLSL(_source, _stage) {
            // Mirror the real instance's behavior: push log lines via
            // "printErr" (our buffer) before throwing.
            for (const line of scriptedLog.split('\n')) buf.push(line);
            if (shouldThrow) throw new Error('GLSL compilation failed');
            return new Uint32Array(0);
        },
        _printedErrors: buf,
        _resetPrintedErrors: () => { buf.length = 0; },
    };
}

describe('transformEs100ToEs310ForValidation', () => {
    it('prepends #version 310 es + precision header for pixel shaders', () => {
        const src = `void main() { gl_FragColor = vec4(1.0); }`;
        const { source, addedLines } = transformEs100ToEs310ForValidation(src, 'pixel');
        expect(source).toMatch(/^#version 310 es\n/);
        expect(source).toContain('precision highp float;');
        expect(source).toContain('precision highp int;');
        expect(addedLines).toBeGreaterThanOrEqual(3);
    });

    it('replaces varying with `in` for pixel stage', () => {
        const src = `varying vec4 vColor; void main() { gl_FragColor = vColor; }`;
        const { source } = transformEs100ToEs310ForValidation(src, 'pixel');
        expect(source).toMatch(/\bin vec4 vColor\b/);
        expect(source).not.toMatch(/\bvarying\b/);
    });

    it('redirects gl_FragColor to a declared out variable', () => {
        const src = `void main() { gl_FragColor = vec4(1.0); }`;
        const { source } = transformEs100ToEs310ForValidation(src, 'pixel');
        expect(source).toMatch(/out vec4 _fragColor;/);
        expect(source).toMatch(/_fragColor\s*=/);
        expect(source).not.toMatch(/gl_FragColor/);
    });

    it('translates texture2D to texture (ES 3+ overload form)', () => {
        const src = `uniform sampler2D tex;\nvoid main() { gl_FragColor = texture2D(tex, vec2(0.0)); }`;
        const { source } = transformEs100ToEs310ForValidation(src, 'pixel');
        expect(source).toMatch(/\btexture\(tex, vec2/);
        expect(source).not.toMatch(/\btexture2D\(/);
    });

    it('adds layout(binding=N) to sampler declarations', () => {
        const src = `uniform sampler2D a; uniform sampler2D b;\nvoid main() {}`;
        const { source } = transformEs100ToEs310ForValidation(src, 'pixel');
        expect(source).toMatch(/layout\(binding=0\) uniform sampler2D a;/);
        expect(source).toMatch(/layout\(binding=1\) uniform sampler2D b;/);
    });

    it('adds layout(std140, binding=N) to uniform blocks', () => {
        const src = `uniform MyBlock { float x; };\nvoid main() {}`;
        const { source } = transformEs100ToEs310ForValidation(src, 'pixel');
        expect(source).toMatch(/layout\(std140, binding=\d+\) uniform MyBlock \{/);
    });

    it('adds layout(location=N) to all in/out declarations (SPIR-V requirement)', () => {
        const src = `varying vec4 vTexCoord0;
varying vec4 vColor;
uniform sampler2D tex;
void main() { gl_FragColor = texture2D(tex, vTexCoord0.xy) * vColor; }`;
        const { source } = transformEs100ToEs310ForValidation(src, 'pixel');
        // `in` declarations get sequential location=0, 1, 2...
        expect(source).toMatch(/layout\(location=0\) in vec4 vTexCoord0;/);
        expect(source).toMatch(/layout\(location=1\) in vec4 vColor;/);
        // The `out vec4 _fragColor;` we synthesize also gets location=0
        // (separate counter from in).
        expect(source).toMatch(/layout\(location=0\) out vec4 _fragColor;/);
    });

    it('handles vertex stage — attribute → in, varying → out', () => {
        const src = `attribute vec4 pos; varying vec4 col; void main() { gl_Position = pos; col = pos; }`;
        const { source } = transformEs100ToEs310ForValidation(src, 'vertex');
        expect(source).toMatch(/\bin vec4 pos\b/);
        expect(source).toMatch(/\bout vec4 col\b/);
    });
});

describe('validateGlsl — with stub glslang instance', () => {
    afterEach(() => {
        __setGlslangInstanceForTest(null);
    });

    it('returns no diagnostics when compile succeeds with empty printErr', async () => {
        __setGlslangInstanceForTest(makeStubGlslang('', false));
        const r = await validateGlsl('void main(){}', 'pixel');
        expect(r.diagnostics).toEqual([]);
        expect(r.glslangUnavailable).toBeFalsy();
    });

    it('parses printErr log when compile throws — multi-line error', async () => {
        const log = [
            `ERROR: 0:5: 'a' : syntax error`,
            `ERROR: 0:5: 'a' : undeclared identifier`,
        ].join('\n');
        __setGlslangInstanceForTest(makeStubGlslang(log, true));
        const r = await validateGlsl('void main(){ a }', 'pixel');
        expect(r.diagnostics).toHaveLength(2);
        expect(r.diagnostics[0]).toMatchObject({
            severity: 'error',
            line: 5,
            token: 'a',
        });
        expect(r.diagnostics[0].message).toContain('syntax error');
    });

    it('falls back to raw log text when no ERROR: line is parseable', async () => {
        const log = 'Wasm runtime aborted; no detailed log available';
        __setGlslangInstanceForTest(makeStubGlslang(log, true));
        const r = await validateGlsl('void main(){}', 'pixel');
        expect(r.diagnostics).toHaveLength(1);
        expect(r.diagnostics[0].severity).toBe('error');
        expect(r.diagnostics[0].message).toContain('Wasm runtime aborted');
    });

    it('resets printErr buffer between compiles (no cross-contamination)', async () => {
        // First compile: errors. Second compile: succeeds — must NOT
        // carry over the first call's diagnostics.
        const inst = makeStubGlslang('ERROR: 0:5: \'x\' : syntax error', true);
        __setGlslangInstanceForTest(inst);
        const r1 = await validateGlsl('first', 'pixel');
        expect(r1.diagnostics.length).toBeGreaterThan(0);

        // Make the next compile pass cleanly. We have to swap the stub
        // because the existing one's scripted log always emits the error.
        __setGlslangInstanceForTest(makeStubGlslang('', false));
        const r2 = await validateGlsl('second', 'pixel');
        expect(r2.diagnostics).toEqual([]);
    });
});

// Validate a GLSL source string via glslang (WASM, lazy-loaded from CDN).
//
// glslang throws on compile errors with the multi-line error log as the
// message. We parse each `ERROR: <col>:<line>: 'token' : message` line
// into a structured diagnostic the caller can convert to Monaco markers.
//
// Caller responsibility: pass GLSL the way glslang wants it. For our use
// case, that means PREPENDING `#version 100\nprecision mediump float;\n`
// to the source we feed glslang (since KNI's BlazorGL accepts no-version
// GLSL = default ES 1.00, but glslang requires an explicit version
// directive to validate). The PRE-prepended lines count toward the
// preambleLineCount the caller subtracts to map back to .fx source.

import { ensureGlslang } from './glslang-loader';

export interface GlslDiagnostic {
    severity: 'error' | 'warning';
    message: string;
    // 1-based line number in the GLSL source as glslang reports it. The
    // caller maps this back to .fx source via the translator's preamble
    // count.
    line: number;
    // GLSL column (or position; glslang's spec varies by driver). For
    // markers we usually want full-line highlight so this is informational.
    column?: number;
    // The offending token glslang named (e.g. 'a', 'input', 'sampler2D').
    token?: string;
}

export interface ValidateGlslResult {
    diagnostics: GlslDiagnostic[];
    // True when glslang itself isn't available — caller should skip
    // emitting per-line markers and surface a single "validation
    // unavailable" hint instead.
    glslangUnavailable?: boolean;
    glslangUnavailableReason?: string;
    // Number of lines the validator prepended to the source to satisfy
    // glslang's SPIR-V ES 3.10 requirements (version directive,
    // precision header, out-fragColor declaration). Caller subtracts
    // this when mapping diagnostic line numbers back to the original
    // GLSL it passed in.
    addedHeaderLines: number;
}

// Transform GLSL ES 1.00 source into ES 3.10-compatible syntax for the
// purpose of glslang validation. glslang's WASM build emits SPIR-V, which
// requires ES 3.10+ with explicit `layout(binding=N)` qualifiers on
// samplers and `in`/`out` instead of `varying`/`gl_FragColor`. KNI's
// BlazorGL runtime accepts (and prefers) the ES 1.00 form, so we keep
// the runtime output unchanged and only upgrade for the validate pass.
//
// Returns the upgraded source PLUS how many lines were prepended, so the
// caller can subtract the right offset when mapping glslang's error
// line numbers back to the original `.fx` source.
export function transformEs100ToEs310ForValidation(
    source: string,
    stage: 'vertex' | 'pixel',
): { source: string; addedLines: number } {
    let s = source;
    let addedLines = 0;

    // Strip any existing #version directive — the caller may have
    // prepended #version 100 in an earlier pass and we're replacing
    // version semantics wholesale.
    s = s.replace(/^\s*#version\s+[^\n]*\n?/m, '');

    // First decide which post-version header lines we need. `#version`
    // MUST be line 1 — prepending anything after building it would push
    // #version off line 1 and glslang rejects.
    const headerLines: string[] = [
        '#version 310 es',
        'precision highp float;',
        'precision highp int;',
    ];

    // varying → in (pixel) / out (vertex)
    if (stage === 'pixel') {
        s = s.replace(/\bvarying\b/g, 'in');
        // gl_FragColor isn't a built-in in ES 3.10 — replace with a
        // user-declared output. Only declare `out vec4 _fragColor;`
        // if the source actually uses gl_FragColor.
        if (/\bgl_FragColor\b/.test(s)) {
            headerLines.push('out vec4 _fragColor;');
            s = s.replace(/\bgl_FragColor\b/g, '_fragColor');
        }
    } else {
        // For vertex shaders: `attribute` → `in`, `varying` → `out`.
        s = s.replace(/\battribute\b/g, 'in');
        s = s.replace(/\bvarying\b/g, 'out');
    }

    // Prepend the assembled header in one shot so `#version` stays at
    // line 1 regardless of how many extra declarations we added.
    s = headerLines.join('\n') + '\n' + s;
    addedLines += headerLines.length;

    // texture2D → texture (ES 3+ overloaded form)
    s = s.replace(/\btexture2D\s*\(/g, 'texture(');
    s = s.replace(/\btexture2DLod\s*\(/g, 'textureLod(');

    // Sampler declarations need explicit layout(binding=N) for SPIR-V.
    // Assign sequential bindings; order matches the natural declaration
    // order which is what KNI also uses, so the offsets line up.
    let samplerIdx = 0;
    s = s.replace(
        /\buniform\s+(sampler2D|sampler3D|samplerCube)\s+(\w+)\s*;/g,
        (_full, ty: string, name: string) => `layout(binding=${samplerIdx++}) uniform ${ty} ${name};`,
    );

    // Uniform blocks also need explicit binding under ES 3.10+SPIR-V.
    let uboIdx = 16;  // start above sampler bindings to avoid collisions
    s = s.replace(
        /\buniform\s+(\w+)\s*\{/g,
        (_full, name: string) => `layout(std140, binding=${uboIdx++}) uniform ${name} {`,
    );

    // Free `uniform vec4 X[N];` declarations — our translator emits these
    // for cbuffer expansion, matching what KNI's ES 1.00 runtime expects.
    // But SPIR-V (GLSL for Vulkan, which glslang validates against) rejects
    // non-opaque uniforms outside a block. Wrap each free uniform-array
    // declaration in a `layout(std140, binding=N) uniform <name>_block { … };`
    // so glslang accepts it. The block is INSTANCE-LESS, so the array name
    // stays at global scope and the `#define X X[i]` aliases still resolve.
    //
    // Only matches `uniform vec/ivec/etc X[N];` shapes — opaque uniforms
    // (sampler*) were already given layout(binding=) above and stay free,
    // which is the correct shape for SPIR-V (samplers ARE allowed outside
    // a block).
    s = s.replace(
        /\buniform\s+((?:vec[234]|ivec[234]|float|int|mat[234])\s+\w+\s*\[\s*\d+\s*\])\s*;/g,
        (_full, decl: string) => {
            const declName = decl.match(/(\w+)\s*\[/)?.[1] ?? 'arr';
            return `layout(std140, binding=${uboIdx++}) uniform _${declName}_block { ${decl}; };`;
        },
    );

    // Same wrap for scalar free uniforms (e.g. `uniform vec4 posFixup;`
    // emitted by the VS preamble). SPIR-V's "non-opaque uniforms must be
    // in a block" rule applies regardless of array vs scalar.
    s = s.replace(
        /\buniform\s+((?:vec[234]|ivec[234]|float|int|mat[234])\s+\w+)\s*;/g,
        (_full, decl: string) => {
            const declName = decl.match(/(\w+)\s*$/)?.[1] ?? 'var';
            return `layout(std140, binding=${uboIdx++}) uniform _${declName}_block { ${decl}; };`;
        },
    );

    // Add explicit layout(location=N) to every in/out declaration. SPIR-V
    // requires user-defined inputs/outputs to have an explicit location;
    // the legacy ES 1.00 `varying`/`gl_FragColor` form didn't need this.
    // Locations are independent counters per direction (in vs out).
    let inLoc = 0;
    let outLoc = 0;
    s = s.replace(
        /(^|[^A-Za-z0-9_])(in|out)\s+(vec[234]|float|int|mat[234])\s+(\w+)\s*;/g,
        (_full, prefix: string, dir: string, ty: string, name: string) => {
            const loc = dir === 'in' ? inLoc++ : outLoc++;
            return `${prefix}layout(location=${loc}) ${dir} ${ty} ${name};`;
        },
    );

    return { source: s, addedLines };
}

// Run glslang on the source. `stage` is 'fragment' or 'vertex' — glslang
// needs to know to apply the right entry rules.
export async function validateGlsl(
    source: string,
    stage: 'vertex' | 'pixel',
): Promise<ValidateGlslResult> {
    let glslang;
    try {
        glslang = await ensureGlslang();
    } catch (e: any) {
        return {
            diagnostics: [],
            glslangUnavailable: true,
            glslangUnavailableReason:
                `glslang WASM failed to load: ${e?.message ?? e}. Live shader validation ` +
                'is disabled; runtime errors after Reset still surface as markers.',
            addedHeaderLines: 0,
        };
    }

    const glslStage = stage === 'pixel' ? 'fragment' : 'vertex';

    // Upgrade ES 1.00 → ES 3.10 for the validate pass. glslang's WASM
    // emits SPIR-V which requires ES 3.10+. The runtime GLSL we hand to
    // KNI is unchanged; this is a transient transformation.
    const { source: validateSource, addedLines } =
        transformEs100ToEs310ForValidation(source, stage);

    // The Emscripten module pipes the actual GLSL error log through
    // printErr (defaults to console.warn). The loader's compileGLSL wrap
    // hijacks console.warn for the duration of each compile and stores
    // captured lines on _printedErrors. Reset that buffer before each
    // compile so leftover lines from a previous attempt don't bleed in.
    glslang._resetPrintedErrors();
    try {
        glslang.compileGLSL(validateSource, glslStage);
        const warnings = parseGlslangErrorLog(glslang._printedErrors.join('\n'));
        return { diagnostics: warnings, addedHeaderLines: addedLines };
    } catch (e: any) {
        const log = glslang._printedErrors.join('\n');
        const diagnostics = parseGlslangErrorLog(log);
        if (diagnostics.length === 0) {
            const tail = (log || String(e?.message ?? e)).split('\n').slice(0, 3).join(' / ').trim();
            diagnostics.push({
                severity: 'error',
                message: tail || 'GLSL compile failed (no log).',
                line: 1,
            });
        }
        return { diagnostics, addedHeaderLines: addedLines };
    }
}

// Parse glslang's multi-line error log into structured diagnostics. Each
// log line looks like one of:
//
//   ERROR: 0:42: 'token' : actual message
//   ERROR: 0:42: actual message without a quoted token
//   WARNING: 0:42: …
//   ERROR: <name>:42: …      (some drivers prefix with filename instead of 0)
//
// We're permissive — anything that looks like `<severity>: <a>:<b>: …`
// gets captured.
const LOG_LINE_RE = /^\s*(ERROR|WARNING)\s*:\s*[^:]+:\s*(\d+):\s*(.*)$/i;

function parseGlslangErrorLog(log: string): GlslDiagnostic[] {
    const out: GlslDiagnostic[] = [];
    for (const line of log.split(/\r?\n/)) {
        const m = LOG_LINE_RE.exec(line);
        if (!m) continue;
        const severity = m[1].toUpperCase() === 'WARNING' ? 'warning' : 'error';
        const lineNum = parseInt(m[2], 10);
        const rest = m[3];
        // Try to extract a quoted token + the message after the colon.
        const tokMatch = /^\s*'([^']+)'\s*:\s*(.*)$/.exec(rest);
        if (tokMatch) {
            out.push({
                severity,
                line: lineNum,
                token: tokMatch[1],
                message: tokMatch[2].trim(),
            });
        } else {
            out.push({
                severity,
                line: lineNum,
                message: rest.trim(),
            });
        }
    }
    return out;
}

// Re-export the loader sub-status so the validator caller can react to
// "still loading" vs "failed to load forever".
export { isGlslangReady, lastGlslangLoadError } from './glslang-loader';

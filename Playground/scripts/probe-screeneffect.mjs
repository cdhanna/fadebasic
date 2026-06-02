// Probe: extract the GLSL bytecode from the known-working ScreenEffect.xnb
// so we can see what shape KNI actually accepts. Wires through the same
// parseEffect + patchEffectMgfxVersionForKni path the playground uses.
//
// Run: node Playground/scripts/probe-screeneffect.mjs

import { readFileSync } from 'node:fs';
import { register } from 'node:module';
import { pathToFileURL } from 'node:url';

// Bootstrap ts-node-equivalent: use tsx-style loader via Vite's esbuild plugin
// hosted in the same node_modules. We just dynamic-import the .ts file through
// the bundler that vitest already uses, since pkg.scripts has vitest set up.
const here = new URL('.', import.meta.url);

// Import the parser via dynamic require of the .ts through a wrapping
// dynamic import — but actually that won't work without a TS loader.
// Easier: re-implement just enough of the MGFX parser to extract one shader's
// bytecode and decode as UTF-8. Doesn't pull in any non-TS deps.

const XNB_PATH = '/Users/chrishanna/Documents/Github/Fade.MonoGame/Fade.MonoGame/Fade.MonoGame/bin/Debug/net10.0/Content/Fish/Shaders/ScreenEffect.xnb';
const bytes = readFileSync(XNB_PATH);

function read7BitInt(view, offset) {
    let result = 0, shift = 0;
    while (true) {
        const b = view[offset++];
        result |= (b & 0x7F) << shift;
        if ((b & 0x80) === 0) break;
        shift += 7;
    }
    return { value: result, offset };
}

function readInt32LE(view, offset) {
    const v = view[offset] | (view[offset + 1] << 8) | (view[offset + 2] << 16) | (view[offset + 3] << 24);
    return { value: v, offset: offset + 4 };
}

function read7BitPrefixedString(view, offset) {
    const { value: len, offset: next } = read7BitInt(view, offset);
    const s = new TextDecoder('utf-8').decode(view.subarray(next, next + len));
    return { value: s, offset: next + len };
}

console.log('Header:');
console.log('  magic   =', String.fromCharCode(bytes[0], bytes[1], bytes[2]), '+', String.fromCharCode(bytes[3]));
console.log('  fmtVer  =', bytes[4]);
console.log('  flags   =', bytes[5].toString(16));
console.log('  fileSz  =', bytes[6] | (bytes[7] << 8) | (bytes[8] << 16) | (bytes[9] << 24));

let off = 10;
const { value: readerCount, offset: o1 } = read7BitInt(bytes, off);
off = o1;
console.log('Readers:', readerCount);
for (let i = 0; i < readerCount; i++) {
    const { value: name, offset: o2 } = read7BitPrefixedString(bytes, off);
    off = o2;
    const { value: ver, offset: o3 } = readInt32LE(bytes, off);
    off = o3;
    console.log(`  [${i}] ${name} v${ver}`);
}
const { value: sharedCount, offset: o4 } = read7BitInt(bytes, off);
off = o4;
console.log('SharedResources:', sharedCount);
const { value: rootTypeId, offset: o5 } = read7BitInt(bytes, off);
off = o5;
console.log('RootTypeId:', rootTypeId);

// EffectReader payload — int32 dataSize, then 'MGFX' magic, version, profile, effectKey, body.
const { value: dataSize, offset: o6 } = readInt32LE(bytes, off);
off = o6;
console.log('MGFX dataSize:', dataSize);
console.log('MGFX magic:', String.fromCharCode(bytes[off], bytes[off+1], bytes[off+2], bytes[off+3]));
const mgfxVersion = bytes[off + 4];
const profileId   = bytes[off + 5];
console.log('  version:', mgfxVersion, '  profile:', profileId, profileId === 0 ? '(OpenGL)' : profileId === 1 ? '(DirectX_11)' : '(other)');

// Skip past magic(4) + version(1) + profile(1) + effectKey(4) = 10 bytes
let cur = off + 10;

const { value: cbufCount, offset: cb0 } = readInt32LE(bytes, cur);
cur = cb0;
console.log('CBuffers:', cbufCount);
for (let i = 0; i < cbufCount; i++) {
    const { value: name, offset: n1 } = read7BitPrefixedString(bytes, cur);
    cur = n1;
    const sizeInBytes = bytes[cur] | (bytes[cur + 1] << 8);
    cur += 2;
    const { value: paramCount, offset: p1 } = readInt32LE(bytes, cur);
    cur = p1;
    console.log(`  cbuf[${i}] ${name}  size=${sizeInBytes}  params=${paramCount}`);
    cur += paramCount * 6;  // (int32 paramIdx + uint16 offset) per param
}

const { value: shaderCount, offset: s0 } = readInt32LE(bytes, cur);
cur = s0;
console.log('Shaders:', shaderCount);
for (let i = 0; i < shaderCount; i++) {
    const isVertex = bytes[cur] !== 0;
    cur += 1;
    // v11 has SourceFile + Entrypoint strings before shaderLength;
    // v10 goes straight to shaderLength.
    if (mgfxVersion >= 11) {
        const { value: sf, offset: sf1 } = read7BitPrefixedString(bytes, cur);
        cur = sf1;
        const { value: ep, offset: ep1 } = read7BitPrefixedString(bytes, cur);
        cur = ep1;
        console.log(`  shader[${i}] ${isVertex ? 'VS' : 'PS'}  source='${sf}'  entry='${ep}'`);
    }
    const { value: shaderLen, offset: l1 } = readInt32LE(bytes, cur);
    cur = l1;
    const shaderBytes = bytes.subarray(cur, cur + shaderLen);
    cur += shaderLen;
    console.log(`  shader[${i}] ${isVertex ? 'VS' : 'PS'}  bytecodeLen=${shaderLen}`);
    console.log(`──── GLSL ────`);
    console.log(new TextDecoder('utf-8').decode(shaderBytes));
    console.log(`──── /GLSL ────`);
    // Skip the rest of the shader record (samplers, cbufferRefs, attributes)
    // to get to the next one.
    const samplerCount = bytes[cur]; cur += 1;
    for (let s = 0; s < samplerCount; s++) {
        cur += 3;  // type, textureSlot, samplerSlot
        const hasState = bytes[cur] !== 0; cur += 1;
        if (hasState) cur += 20;
        const { offset: n1 } = read7BitPrefixedString(bytes, cur);
        cur = n1;
        cur += 1;  // parameterIndex
    }
    const cbufRefCount = bytes[cur]; cur += 1;
    cur += cbufRefCount;
    const attrCount = bytes[cur]; cur += 1;
    for (let a = 0; a < attrCount; a++) {
        const { offset: n1 } = read7BitPrefixedString(bytes, cur);
        cur = n1;
        cur += 4;  // usage + index + int16 location
    }
}

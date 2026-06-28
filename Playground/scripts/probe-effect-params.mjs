// Probe: dump the parameter records inside an effect XNB so we can see
// what `class_`/`type` values KNI's working ScreenEffect actually uses.
// Tells us what EffectParameterClass values to emit for float4/float/etc.
//
// Run: node Playground/scripts/probe-effect-params.mjs [<xnb-path>]

import { readFileSync } from 'node:fs';

const XNB_PATH = process.argv[2] ?? '/Users/chrishanna/Documents/Github/Fade.MonoGame/Fade.MonoGame/Fade.MonoGame/bin/Debug/net10.0/Content/Fish/Shaders/ScreenEffect.xnb';

const CLASS_NAMES = ['Scalar', 'Vector', 'Matrix', 'Object', 'Struct'];
const TYPE_NAMES = ['Void', 'Bool', 'Int32', 'Single', 'String', 'Texture', 'Texture1D', 'Texture2D', 'Texture3D', 'TextureCube'];

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
    return { value: view[offset] | (view[offset + 1] << 8) | (view[offset + 2] << 16) | (view[offset + 3] << 24), offset: offset + 4 };
}
function readUint16LE(view, offset) {
    return { value: view[offset] | (view[offset + 1] << 8), offset: offset + 2 };
}
function read7BitPrefixedString(view, offset) {
    const { value: len, offset: next } = read7BitInt(view, offset);
    return { value: new TextDecoder('utf-8').decode(view.subarray(next, next + len)), offset: next + len };
}

// XNB header
let off = 10;
const { value: readerCount, offset: o1 } = read7BitInt(bytes, off); off = o1;
for (let i = 0; i < readerCount; i++) {
    const r = read7BitPrefixedString(bytes, off); off = r.offset;
    off += 4; // reader version int32
}
off += 1; // shared resource count 7bit (0)
off += 1; // root type id 7bit (1)

// EffectReader payload
const { value: dataSize, offset: o6 } = readInt32LE(bytes, off); off = o6;
console.log(`MGFX blob: ${dataSize} bytes, version=${bytes[off + 4]}, profile=${bytes[off + 5]}`);
let cur = off + 10; // magic(4) + version(1) + profile(1) + effectKey(4)

// cbuffers
const { value: cbufCount, offset: cb0 } = readInt32LE(bytes, cur); cur = cb0;
console.log(`\nCBuffers: ${cbufCount}`);
const cbufs = [];
for (let i = 0; i < cbufCount; i++) {
    const n = read7BitPrefixedString(bytes, cur); cur = n.offset;
    const sz = readUint16LE(bytes, cur); cur = sz.offset;
    const pc = readInt32LE(bytes, cur); cur = pc.offset;
    const params = [];
    for (let p = 0; p < pc.value; p++) {
        const idx = readInt32LE(bytes, cur); cur = idx.offset;
        const ofs = readUint16LE(bytes, cur); cur = ofs.offset;
        params.push({ paramIdx: idx.value, offset: ofs.value });
    }
    console.log(`  [${i}] ${n.value}  size=${sz.value}  params=`, params);
    cbufs.push({ name: n.value, params });
}

// shaders — walk over them to reach the parameter list
const { value: shaderCount, offset: s0 } = readInt32LE(bytes, cur); cur = s0;
console.log(`\nShaders: ${shaderCount} (skipping past)`);
const mgfxVersion = bytes[off + 4];
for (let i = 0; i < shaderCount; i++) {
    cur += 1; // isVertexShader
    if (mgfxVersion >= 11) {
        const sf = read7BitPrefixedString(bytes, cur); cur = sf.offset;
        const ep = read7BitPrefixedString(bytes, cur); cur = ep.offset;
    }
    const len = readInt32LE(bytes, cur); cur = len.offset;
    cur += len.value;
    const samCount = bytes[cur]; cur += 1;
    for (let s = 0; s < samCount; s++) {
        cur += 3; // type, textureSlot, samplerSlot
        const hasState = bytes[cur] !== 0; cur += 1;
        if (hasState) cur += 20;
        const nm = read7BitPrefixedString(bytes, cur); cur = nm.offset;
        cur += 1; // parameterIndex
    }
    const cbRefCount = bytes[cur]; cur += 1; cur += cbRefCount;
    const attrCount = bytes[cur]; cur += 1;
    for (let a = 0; a < attrCount; a++) {
        const n = read7BitPrefixedString(bytes, cur); cur = n.offset;
        cur += 4; // usage + index + int16 location
    }
}

// PARAMETERS
function readParams(depth = 0) {
    const { value: count } = readInt32LE(bytes, cur); cur += 4;
    const out = [];
    for (let i = 0; i < count; i++) {
        const cls = bytes[cur]; cur += 1;
        const typ = bytes[cur]; cur += 1;
        const nm = read7BitPrefixedString(bytes, cur); cur = nm.offset;
        const sem = read7BitPrefixedString(bytes, cur); cur = sem.offset;
        const annotations = readParams(depth + 1);
        const rows = bytes[cur]; cur += 1;
        const cols = bytes[cur]; cur += 1;
        const elements = readParams(depth + 1);
        const members = readParams(depth + 1);
        let dataLen = 0;
        if (elements.length === 0 && members.length === 0) {
            if (typ === 1 || typ === 2 || typ === 3) {
                dataLen = rows * cols * 4;
                cur += dataLen;
            }
        }
        out.push({
            name: nm.value,
            class_: cls,
            className: CLASS_NAMES[cls] ?? `?${cls}`,
            type: typ,
            typeName: TYPE_NAMES[typ] ?? `?${typ}`,
            semantic: sem.value,
            rows, columns: cols,
            dataLen,
        });
    }
    return out;
}

const topParams = readParams();
console.log(`\nParameters: ${topParams.length}`);
for (const p of topParams) {
    console.log(`  ${p.name}: class=${p.className}(${p.class_}) type=${p.typeName}(${p.type}) rows=${p.rows} cols=${p.columns} dataLen=${p.dataLen}`);
}

// Techniques + passes
const { value: techCount, offset: t0 } = readInt32LE(bytes, cur); cur = t0;
console.log(`\nTechniques: ${techCount}`);
for (let i = 0; i < techCount; i++) {
    const nm = read7BitPrefixedString(bytes, cur); cur = nm.offset;
    // annotations (skip — same recursive params shape)
    const _anns = readParams();
    const { value: passCount, offset: p0 } = readInt32LE(bytes, cur); cur = p0;
    console.log(`  technique '${nm.value}'  passes=${passCount}`);
    for (let p = 0; p < passCount; p++) {
        const pn = read7BitPrefixedString(bytes, cur); cur = pn.offset;
        const _panns = readParams();
        const { value: vsIdx, offset: v0 } = readInt32LE(bytes, cur); cur = v0;
        const { value: psIdx, offset: p1 } = readInt32LE(bytes, cur); cur = p1;
        const hasBlend = bytes[cur] !== 0; cur += 1; if (hasBlend) cur += 18;
        const hasDepth = bytes[cur] !== 0; cur += 1; if (hasDepth) cur += 24;
        const hasRaster = bytes[cur] !== 0; cur += 1; if (hasRaster) cur += 12;
        console.log(`    pass '${pn.value}'  vsShaderIndex=${vsIdx}  psShaderIndex=${psIdx}  blend=${hasBlend} depth=${hasDepth} raster=${hasRaster}`);
    }
}

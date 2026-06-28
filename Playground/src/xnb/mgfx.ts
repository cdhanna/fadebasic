// Full MGFX v10 parser / emitter.
//
// Wire format from:
//   MonoGame EffectObject.writer.cs — MGFX writer
//   MonoGame Effect.cs ReadEffect() — MGFX reader
//   MonoGame Shader.cs constructor  — Shader record reader
//
// Exported surface:
//   parseEffect(objectData)  → MgfxEffect
//   emitEffect(MgfxEffect)   → objectData bytes (int32 dataSize prefix + MGFX blob)
//   roundTripXnb(bytes)      → full XNB bytes (parse + re-emit; byte-identical for v10 input)

import { ByteCursor, XnbParseError, classifyXnb } from './xnb-reader';

// ── Types ────────────────────────────────────────────────────────────────────

export interface MgfxConstantBuffer {
    name: string;
    sizeInBytes: number;  // int16 on wire
    params: Array<{ paramIdx: number; offset: number }>;
}

export interface MgfxSampler {
    type: number;
    textureSlot: number;
    samplerSlot: number;
    stateBytes: Uint8Array | null;  // null → hasState=false; 20 bytes when present
    name: string;
    parameterIndex: number;
}

export interface MgfxAttribute {
    name: string;
    usage: number;
    index: number;
    location: number;  // int16; may be negative
}

export interface MgfxShader {
    isVertexShader: boolean;
    bytecode: Uint8Array;
    samplers: MgfxSampler[];
    cbufferRefs: number[];
    attributes: MgfxAttribute[];
}

// EffectParameterType enum values — mirrors MonoGame's EffectParameterType.cs.
export const EPT = {
    Void: 0, Bool: 1, Int32: 2, Single: 3,
    String: 4, Texture: 5, Texture1D: 6, Texture2D: 7,
    Texture3D: 8, TextureCube: 9,
} as const;

export interface MgfxParam {
    class_: number;
    type: number;
    name: string;
    semantic: string;
    annotations: MgfxParam[];
    rows: number;
    columns: number;
    elements: MgfxParam[];  // for array types
    members: MgfxParam[];   // for struct types
    // rows*columns*4 raw bytes for Bool/Int32/Single leaf params; null otherwise.
    data: Uint8Array | null;
}

export interface MgfxPass {
    name: string;
    annotations: MgfxParam[];
    vsShaderIndex: number;    // -1 = no vertex shader
    psShaderIndex: number;    // -1 = no pixel shader
    blendBytes: Uint8Array | null;   // 18 bytes when present
    depthBytes: Uint8Array | null;   // 24 bytes when present
    rasterBytes: Uint8Array | null;  // 12 bytes when present
}

export interface MgfxTechnique {
    name: string;
    annotations: MgfxParam[];
    passes: MgfxPass[];
}

export interface MgfxEffect {
    version: number;    // 10 for v10
    profileId: number;  // 0=OpenGL, 1=DX11, 3=Vulkan
    effectKey: number;  // int32 content hash; preserved as-is on round-trip
    constantBuffers: MgfxConstantBuffer[];
    shaders: MgfxShader[];
    parameters: MgfxParam[];
    techniques: MgfxTechnique[];
    // Opaque bytes between the last technique and the real MGFX tail.
    // The MGFX format (at least in MGCB v11 / KNI) writes additional data
    // here that we don't need to interpret; we preserve it verbatim so that
    // round-trip output is byte-identical to the input.
    trailingBody: Uint8Array;
}

// ── State-block byte sizes (fixed layout from EffectObject.writer.cs) ────────

const SAMPLER_STATE_BYTES = 20;  // addr×3 + color×4 + filter + aniso(4) + mip(4) + bias(4)
const BLEND_STATE_BYTES   = 18;  // 14 bytes + MultiSampleMask int32
const DEPTH_STATE_BYTES   = 24;  // various bools/bytes + 3×int32
const RASTER_STATE_BYTES  = 12;  // cull + depthBias(4) + fill + 2×bool + slopeScale(4)

// ── Parser ───────────────────────────────────────────────────────────────────

// Parse from XNB objectData (starts with int32 dataSize, then the MGFX blob).
export function parseEffect(objectData: Uint8Array): MgfxEffect {
    const od = objectData;
    if (od.length < 14) throw new XnbParseError('MGFX objectData too small');
    if (od[4] !== 0x4D || od[5] !== 0x47 || od[6] !== 0x46 || od[7] !== 0x58)
        throw new XnbParseError('Missing MGFX magic at objectData[4]');

    const version  = od[8];
    const profileId = od[9];
    // effectKey is int32 at objectData[10..13] — preserved as-is
    const effectKey = asInt32(od[10] | (od[11] << 8) | (od[12] << 16) | (od[13] << 24));

    const cur = new ByteCursor(od, 14);

    const cbufCount = cur.readInt32LE();
    const constantBuffers: MgfxConstantBuffer[] = [];
    for (let i = 0; i < cbufCount; i++) {
        const name = cur.read7BitPrefixedString();
        const sizeInBytes = signedInt16(cur.readUint16LE());
        const paramCount = cur.readInt32LE();
        const params: Array<{ paramIdx: number; offset: number }> = [];
        for (let p = 0; p < paramCount; p++)
            params.push({ paramIdx: cur.readInt32LE(), offset: cur.readUint16LE() });
        constantBuffers.push({ name, sizeInBytes, params });
    }

    const shaderCount = cur.readInt32LE();
    const shaders: MgfxShader[] = [];
    for (let i = 0; i < shaderCount; i++) shaders.push(readShader(cur));

    const parameters = readParams(cur);

    const techniqueCount = cur.readInt32LE();
    const techniques: MgfxTechnique[] = [];
    for (let i = 0; i < techniqueCount; i++) techniques.push(readTechnique(cur));

    const tail = cur.readBytes(4);
    if (tail[0] !== 0x4D || tail[1] !== 0x47 || tail[2] !== 0x46 || tail[3] !== 0x58)
        throw new XnbParseError('Missing MGFX tail');

    // Capture any bytes that follow the tail magic to end of objectData.
    // MGCB includes extra data here (apparently compile metadata or padding)
    // that neither the format spec nor KNI's reader processes, but dataSize
    // counts it.  Preserve it verbatim so round-trip output is byte-identical.
    const trailingBody = cur.offset < od.length
        ? od.subarray(cur.offset).slice()
        : new Uint8Array(0);

    return { version, profileId, effectKey, constantBuffers, shaders, parameters, techniques, trailingBody };
}

function readShader(cur: ByteCursor): MgfxShader {
    const isVertexShader = cur.readUint8() !== 0;
    const shaderLength = cur.readInt32LE();
    const bytecode = cur.readBytes(shaderLength).slice();

    const samplerCount = cur.readUint8();
    const samplers: MgfxSampler[] = [];
    for (let i = 0; i < samplerCount; i++) {
        const type        = cur.readUint8();
        const textureSlot = cur.readUint8();
        const samplerSlot = cur.readUint8();
        const hasState    = cur.readUint8() !== 0;
        const stateBytes  = hasState ? cur.readBytes(SAMPLER_STATE_BYTES).slice() : null;
        const name           = cur.read7BitPrefixedString();
        const parameterIndex = cur.readUint8();
        samplers.push({ type, textureSlot, samplerSlot, stateBytes, name, parameterIndex });
    }

    const cbufRefCount = cur.readUint8();
    const cbufferRefs: number[] = [];
    for (let i = 0; i < cbufRefCount; i++) cbufferRefs.push(cur.readUint8());

    const attrCount = cur.readUint8();
    const attributes: MgfxAttribute[] = [];
    for (let i = 0; i < attrCount; i++) {
        const name     = cur.read7BitPrefixedString();
        const usage    = cur.readUint8();
        const index    = cur.readUint8();
        const location = signedInt16(cur.readUint16LE());
        attributes.push({ name, usage, index, location });
    }

    return { isVertexShader, bytecode, samplers, cbufferRefs, attributes };
}

function readParams(cur: ByteCursor): MgfxParam[] {
    const count = cur.readInt32LE();
    const params: MgfxParam[] = [];
    for (let i = 0; i < count; i++) params.push(readParam(cur));
    return params;
}

function readParam(cur: ByteCursor): MgfxParam {
    const class_      = cur.readUint8();
    const type        = cur.readUint8();
    const name        = cur.read7BitPrefixedString();
    const semantic    = cur.read7BitPrefixedString();
    const annotations = readParams(cur);
    const rows        = cur.readUint8();
    const columns     = cur.readUint8();
    const elements    = readParams(cur);
    const members     = readParams(cur);

    let data: Uint8Array | null = null;
    if (elements.length === 0 && members.length === 0) {
        // On OpenGL (which KNI uses) Bool/Int32 fall through to Single in
        // the reader — all three are stored as rows*columns float32 values.
        if (type === EPT.Bool || type === EPT.Int32 || type === EPT.Single) {
            data = cur.readBytes(rows * columns * 4).slice();
        }
    }

    return { class_, type, name, semantic, annotations, rows, columns, elements, members, data };
}

function readTechnique(cur: ByteCursor): MgfxTechnique {
    const name        = cur.read7BitPrefixedString();
    const annotations = readParams(cur);
    const passCount   = cur.readInt32LE();
    const passes: MgfxPass[] = [];
    for (let i = 0; i < passCount; i++) passes.push(readPass(cur));
    return { name, annotations, passes };
}

function readPass(cur: ByteCursor): MgfxPass {
    const name          = cur.read7BitPrefixedString();
    const annotations   = readParams(cur);
    const vsShaderIndex = cur.readInt32LE();
    const psShaderIndex = cur.readInt32LE();
    const blendBytes  = cur.readUint8() !== 0 ? cur.readBytes(BLEND_STATE_BYTES).slice() : null;
    const depthBytes  = cur.readUint8() !== 0 ? cur.readBytes(DEPTH_STATE_BYTES).slice() : null;
    const rasterBytes = cur.readUint8() !== 0 ? cur.readBytes(RASTER_STATE_BYTES).slice() : null;
    return { name, annotations, vsShaderIndex, psShaderIndex, blendBytes, depthBytes, rasterBytes };
}

function signedInt16(v: number): number {
    return v > 0x7FFF ? v - 0x10000 : v;
}
function asInt32(v: number): number {
    return v | 0;
}

// ── Emitter ──────────────────────────────────────────────────────────────────

class ByteWriter {
    private buf: number[] = [];

    writeUint8(v: number)  { this.buf.push(v & 0xFF); }
    writeBool(v: boolean)  { this.buf.push(v ? 1 : 0); }
    writeUint16LE(v: number) {
        this.buf.push(v & 0xFF, (v >>> 8) & 0xFF);
    }
    writeInt16LE(v: number) {
        const u = v < 0 ? v + 0x10000 : v;
        this.buf.push(u & 0xFF, (u >>> 8) & 0xFF);
    }
    writeInt32LE(v: number) {
        this.buf.push(
            v & 0xFF, (v >>> 8) & 0xFF,
            (v >>> 16) & 0xFF, (v >>> 24) & 0xFF,
        );
    }
    writeBytes(bytes: Uint8Array) {
        for (let i = 0; i < bytes.length; i++) this.buf.push(bytes[i]);
    }
    write7BitInt(v: number) {
        while (v >= 0x80) { this.buf.push((v & 0x7F) | 0x80); v >>>= 7; }
        this.buf.push(v);
    }
    write7BitPrefixedString(s: string) {
        const enc = new TextEncoder().encode(s);
        this.write7BitInt(enc.length);
        for (const b of enc) this.buf.push(b);
    }
    writeMgfxMagic() { this.buf.push(0x4D, 0x47, 0x46, 0x58); }
    get length() { return this.buf.length; }
    toUint8Array() { return new Uint8Array(this.buf); }
}

// Emit to XNB objectData bytes (int32 dataSize prefix + MGFX blob).
export function emitEffect(effect: MgfxEffect): Uint8Array {
    const body = new ByteWriter();

    body.writeInt32LE(effect.constantBuffers.length);
    for (const cb of effect.constantBuffers) {
        body.write7BitPrefixedString(cb.name);
        body.writeInt16LE(cb.sizeInBytes);
        body.writeInt32LE(cb.params.length);
        for (const p of cb.params) {
            body.writeInt32LE(p.paramIdx);
            body.writeUint16LE(p.offset);
        }
    }

    body.writeInt32LE(effect.shaders.length);
    for (const s of effect.shaders) writeShader(body, s);

    writeParams(body, effect.parameters);

    body.writeInt32LE(effect.techniques.length);
    for (const t of effect.techniques) writeTechnique(body, t);

    body.writeMgfxMagic();  // tail

    if (effect.trailingBody.length > 0) body.writeBytes(effect.trailingBody);

    // objectData layout: int32 dataSize + MGFX blob
    // MGFX blob: magic(4) + version(1) + profile(1) + effectKey(4) + body
    // (body above already includes the tail)
    const bodyBytes = body.toUint8Array();
    const dataSize = 10 + bodyBytes.length;  // 10 = magic+version+profile+effectKey

    const out = new ByteWriter();
    out.writeInt32LE(dataSize);
    out.writeMgfxMagic();
    out.writeUint8(effect.version);
    out.writeUint8(effect.profileId);
    out.writeInt32LE(effect.effectKey);
    out.writeBytes(bodyBytes);
    return out.toUint8Array();
}

function writeShader(w: ByteWriter, s: MgfxShader) {
    w.writeBool(s.isVertexShader);
    w.writeInt32LE(s.bytecode.length);
    w.writeBytes(s.bytecode);

    w.writeUint8(s.samplers.length);
    for (const sam of s.samplers) {
        w.writeUint8(sam.type);
        w.writeUint8(sam.textureSlot);
        w.writeUint8(sam.samplerSlot);
        w.writeBool(sam.stateBytes !== null);
        if (sam.stateBytes) w.writeBytes(sam.stateBytes);
        w.write7BitPrefixedString(sam.name);
        w.writeUint8(sam.parameterIndex);
    }

    w.writeUint8(s.cbufferRefs.length);
    for (const r of s.cbufferRefs) w.writeUint8(r);

    w.writeUint8(s.attributes.length);
    for (const a of s.attributes) {
        w.write7BitPrefixedString(a.name);
        w.writeUint8(a.usage);
        w.writeUint8(a.index);
        w.writeInt16LE(a.location);
    }
}

function writeParams(w: ByteWriter, params: MgfxParam[]) {
    w.writeInt32LE(params.length);
    for (const p of params) writeParam(w, p);
}

function writeParam(w: ByteWriter, p: MgfxParam) {
    w.writeUint8(p.class_);
    w.writeUint8(p.type);
    w.write7BitPrefixedString(p.name);
    w.write7BitPrefixedString(p.semantic);
    writeParams(w, p.annotations);
    w.writeUint8(p.rows);
    w.writeUint8(p.columns);
    writeParams(w, p.elements);
    writeParams(w, p.members);
    if (p.data) w.writeBytes(p.data);
}

function writeTechnique(w: ByteWriter, t: MgfxTechnique) {
    w.write7BitPrefixedString(t.name);
    writeParams(w, t.annotations);
    w.writeInt32LE(t.passes.length);
    for (const p of t.passes) writePass(w, p);
}

function writePass(w: ByteWriter, p: MgfxPass) {
    w.write7BitPrefixedString(p.name);
    writeParams(w, p.annotations);
    w.writeInt32LE(p.vsShaderIndex);
    w.writeInt32LE(p.psShaderIndex);
    w.writeBool(p.blendBytes !== null);
    if (p.blendBytes)  w.writeBytes(p.blendBytes);
    w.writeBool(p.depthBytes !== null);
    if (p.depthBytes)  w.writeBytes(p.depthBytes);
    w.writeBool(p.rasterBytes !== null);
    if (p.rasterBytes) w.writeBytes(p.rasterBytes);
}

// ── Round-trip ───────────────────────────────────────────────────────────────

// Parse a v10 effect XNB and re-emit it. Output is byte-identical to input
// for any well-formed v10 XNB, so callers can byte-diff to validate the parser.
// Throws if the input is not an effect XNB or fails to parse.
export function roundTripXnb(bytes: Uint8Array): Uint8Array {
    const cls = classifyXnb(bytes);
    if (cls.kind !== 'effect' || !cls.objectData)
        throw new Error('roundTripXnb: not an effect XNB (or compressed)');

    const objectData = cls.objectData;
    const payloadStart = objectData.byteOffset - bytes.byteOffset;

    // dataSize is the int32 at objectData[0:4] — the length of the MGFX blob
    // (does not include the 4-byte dataSize field itself).  Everything after
    // payloadStart + 4 + dataSize is trailing XNB data (shared resources etc.)
    // that has nothing to do with the MGFX object and must be preserved.
    const originalDataSize = (objectData[0] | (objectData[1] << 8) | (objectData[2] << 16) | (objectData[3] << 24)) >>> 0;
    const originalObjectEnd = payloadStart + 4 + originalDataSize;

    const effect = parseEffect(objectData);
    const newObjectData = emitEffect(effect);

    const trailing = bytes.length - originalObjectEnd;
    const out = new Uint8Array(payloadStart + newObjectData.length + trailing);
    out.set(bytes.subarray(0, payloadStart));
    out.set(newObjectData, payloadStart);
    if (trailing > 0) out.set(bytes.subarray(originalObjectEnd), payloadStart + newObjectData.length);

    // Update XNB fileSize at bytes[6..9]
    const newFileSize = out.length;
    out[6] = newFileSize & 0xFF;
    out[7] = (newFileSize >>> 8) & 0xFF;
    out[8] = (newFileSize >>> 16) & 0xFF;
    out[9] = (newFileSize >>> 24) & 0xFF;

    return out;
}


// Audio source (WAV/MP3/OGG/FLAC/AAC) → SoundEffect XNB encoder.
//
// File layout we emit (matches the on-disk shape MonoGame's
// SoundEffectReader and the existing patchXnbForKni already cover):
//
//   'XNB'              3 bytes
//   platform byte      'd' (DesktopGL)
//   format version     5
//   flags              0
//   uint32 LE          fileSize (whole file)
//
//   varint             reader count (1)
//   varlen UTF-8       "Microsoft.Xna.Framework.Content.SoundEffectReader"
//   int32              reader version (0)
//   varint             shared resource count (0)
//   varint             root object type id (1)
//
//   int32              headerSize (18 for PCM WAVEFORMATEX with cbSize=0)
//   WAVEFORMATEX:
//     uint16           wFormatTag       (1 = PCM)
//     uint16           nChannels        (1 = mono, 2 = stereo)
//     uint32           nSamplesPerSec
//     uint32           nAvgBytesPerSec
//     uint16           nBlockAlign      (channels * bytesPerSample)
//     uint16           wBitsPerSample   (16)
//     uint16           cbSize           (0; no format-specific extra bytes)
//   int32              dataSize         (length of audio bytes)
//   bytes              audio data       (interleaved 16-bit PCM, little-endian)
//   int32              loopStart        (0)
//   int32              loopLength       (totalFrames — KNI reads this for
//                                        playback length even on non-looped
//                                        sounds; see patchXnbForKni)
//
// Decoding: the source bytes can be any format the browser's
// AudioContext.decodeAudioData accepts (WAV, MP3, OGG on Firefox/
// Chrome/Edge, FLAC, AAC/M4A). All formats land on AudioBuffer's
// Float32 PCM, which we then quantise to Int16 for the XNB payload.

import { ADAPT_COEFFS_INT16, encodeMsAdpcm } from './adpcm-encoder';

const SOUND_EFFECT_READER = 'Microsoft.Xna.Framework.Content.SoundEffectReader';

// MS-ADPCM convention block sizes that line up with the values MGCB
// would emit at standard SoundEffectProcessor.Quality. Bigger blocks
// amortise the 14-byte per-block stereo header better; smaller blocks
// recover faster from a bad predictor choice. The numbers below are
// inside KNI's tested envelope and stay multiples of 4 for alignment.
const ADPCM_BLOCK_ALIGN_MONO   = 512;
const ADPCM_BLOCK_ALIGN_STEREO = 1024;

export interface DecodedAudio {
    /** Interleaved 16-bit PCM samples (host-endian; browsers are all LE). */
    samples: Int16Array;
    sampleRate: number;
    channels: number;
    /** length / sampleRate, rounded to 3 places. */
    duration: number;
}

// Lazy AudioContext singleton. Web Audio's autoplay policy only gates
// the playback path (resume()/start()); decodeAudioData runs fine
// without user activation on every browser I know of. Creating one here
// avoids forcing every caller to plumb a shared context around.
let _ctx: AudioContext | null = null;
function getAudioContext(): AudioContext {
    if (_ctx) return _ctx;
    const Ctor: typeof AudioContext | undefined =
        (globalThis as any).AudioContext ?? (globalThis as any).webkitAudioContext;
    if (!Ctor) {
        throw new Error('Web Audio API is not available in this environment.');
    }
    _ctx = new Ctor();
    return _ctx;
}

/** Decode any audio bytes into interleaved 16-bit PCM. Throws when the
 *  browser refuses to decode (e.g. OGG on Safari) — the caller turns
 *  that into a per-asset diagnostic. */
export async function decodeAudio(bytes: Uint8Array): Promise<DecodedAudio> {
    const ctx = getAudioContext();
    // decodeAudioData detaches the ArrayBuffer it receives. We slice to
    // a fresh plain ArrayBuffer so callers (and the cache) keep their
    // byte-source intact across repeated decodes. The intermediate
    // `new ArrayBuffer + Uint8Array.set` avoids SharedArrayBuffer typing
    // issues when this module runs in a cross-origin-isolated context.
    const ab = new ArrayBuffer(bytes.byteLength);
    new Uint8Array(ab).set(bytes);
    const audio = await ctx.decodeAudioData(ab);

    const { numberOfChannels: channels, length, sampleRate } = audio;
    const samples = new Int16Array(length * channels);
    // Pull per-channel Float32 data once; interleave + quantise as we go.
    const channelData: Float32Array[] = [];
    for (let c = 0; c < channels; c++) channelData.push(audio.getChannelData(c));
    for (let i = 0; i < length; i++) {
        for (let c = 0; c < channels; c++) {
            let s = channelData[c][i];
            // Hard clip; the asymmetric scale (32767/-32768) is the
            // standard XAudio2 convention to use the full range without
            // rounding -1.0 down to -32769.
            if (s >  1) s =  1;
            if (s < -1) s = -1;
            samples[i * channels + c] = s < 0
                ? Math.round(s * 0x8000)
                : Math.round(s * 0x7FFF);
        }
    }
    return {
        samples,
        sampleRate,
        channels,
        duration: Math.round((length / sampleRate) * 1000) / 1000,
    };
}

/** Common low-level XNB-wrapping for both PCM and MS-ADPCM SoundEffect
 *  XNBs. `formatExtra` carries the cbSize-prefixed bytes for
 *  format-specific header data (empty for PCM; the
 *  samplesPerBlock+coeffs payload for ADPCM). `totalFrames` /
 *  `durationMs` close out the trailing SoundEffect fields KNI's reader
 *  consumes — all three int32s (loopStart, loopLength, durationMs) MUST
 *  be present or `SoundEffectReader.Read` runs off the end of the
 *  stream with an EndOfStreamException at load time. */
function buildSoundEffectXnb(opts: {
    wFormatTag: number;
    channels: number;
    sampleRate: number;
    avgBytesPerSec: number;
    blockAlign: number;
    bitsPerSample: number;
    formatExtra: Uint8Array;
    audio: Uint8Array;
    totalFrames: number;
    durationMs: number;
}): Uint8Array {
    const readerName = utf8.encode(SOUND_EFFECT_READER);
    const meta: number[] = [];
    write7BitInt(meta, 1);                       // reader count
    write7BitInt(meta, readerName.length);
    for (const b of readerName) meta.push(b);
    pushInt32LE(meta, 0);                         // reader version
    write7BitInt(meta, 0);                         // shared resource count
    write7BitInt(meta, 1);                         // root object type id

    const cbSize = opts.formatExtra.length;
    pushInt32LE(meta, 18 + cbSize);               // headerSize (WAVEFORMATEX + extra)
    pushInt16LE(meta, opts.wFormatTag);
    pushInt16LE(meta, opts.channels);
    pushInt32LE(meta, opts.sampleRate);
    pushInt32LE(meta, opts.avgBytesPerSec);
    pushInt16LE(meta, opts.blockAlign);
    pushInt16LE(meta, opts.bitsPerSample);
    pushInt16LE(meta, cbSize);
    for (let i = 0; i < opts.formatExtra.length; i++) meta.push(opts.formatExtra[i]);

    pushInt32LE(meta, opts.audio.length);          // dataSize

    const trailing: number[] = [];
    pushInt32LE(trailing, 0);                      // loopStart
    pushInt32LE(trailing, opts.totalFrames);       // loopLength (full sample)
    pushInt32LE(trailing, opts.durationMs);        // durationMs — KNI's
    // SoundEffectReader.Read consumes THREE trailing int32s, not two.
    // Omitting this one triggers EndOfStreamException at load time
    // even though the audio bytes themselves are fine.

    const xnbHeaderSize = 10;
    const fileSize = xnbHeaderSize + meta.length + opts.audio.length + trailing.length;
    const out = new Uint8Array(fileSize);
    out[0] = 0x58; out[1] = 0x4E; out[2] = 0x42;   // 'XNB'
    out[3] = 0x64;                                 // 'd' DesktopGL
    out[4] = 5;                                    // format version
    out[5] = 0;                                    // flags
    writeUint32LE(out, 6, fileSize);

    let offset = xnbHeaderSize;
    for (let i = 0; i < meta.length; i++) out[offset++] = meta[i];
    out.set(opts.audio, offset);
    offset += opts.audio.length;
    for (let i = 0; i < trailing.length; i++) out[offset++] = trailing[i];
    return out;
}

/** Serialise a decoded clip as a SoundEffect XNB carrying PCM bytes. */
export function encodePcmSoundEffectXnb(decoded: DecodedAudio): Uint8Array {
    const { samples, sampleRate, channels } = decoded;
    if (channels < 1 || channels > 2) {
        throw new Error(`audio-encoder: ${channels}-channel source — only mono/stereo supported.`);
    }
    const bitsPerSample = 16;
    const blockAlign = channels * (bitsPerSample / 8);
    const audio = new Uint8Array(samples.buffer, samples.byteOffset, samples.byteLength);
    const totalFrames = samples.length / channels;
    return buildSoundEffectXnb({
        wFormatTag: 1,                                // PCM
        channels,
        sampleRate,
        avgBytesPerSec: sampleRate * blockAlign,
        blockAlign,
        bitsPerSample,
        formatExtra: new Uint8Array(0),
        audio,
        totalFrames,
        durationMs: Math.round((totalFrames / sampleRate) * 1000),
    });
}

/** Serialise a decoded clip as a SoundEffect XNB carrying MS-ADPCM
 *  (wFormatTag=2) bytes. The 32-byte format-specific suffix carries the
 *  per-block sample count and the AdaptCoeff table KNI's decoder uses to
 *  reconstruct samples — same table the encoder picks predictors from. */
export function encodeAdpcmSoundEffectXnb(decoded: DecodedAudio): Uint8Array {
    const { samples, sampleRate, channels } = decoded;
    if (channels < 1 || channels > 2) {
        throw new Error(`audio-encoder: ${channels}-channel source — only mono/stereo supported.`);
    }
    const blockAlign = channels === 1
        ? ADPCM_BLOCK_ALIGN_MONO
        : ADPCM_BLOCK_ALIGN_STEREO;
    const adpcm = encodeMsAdpcm(samples, channels, blockAlign);

    // avgBytesPerSec for ADPCM: bytes per block × blocks per second.
    // sampleRate / samplesPerBlock gives blocks-per-second exactly.
    const avgBytesPerSec = Math.round(
        (sampleRate / adpcm.samplesPerBlock) * adpcm.blockAlign,
    );

    // Format-specific suffix (cbSize=32 bytes):
    //   uint16 samplesPerBlock
    //   uint16 numCoef (7)
    //   int16  aCoef[7][2]  — exactly ADAPT_COEFFS_INT16
    const numCoef = ADAPT_COEFFS_INT16.length;
    const extraBytes = 4 + numCoef * 4;            // 4 + 28 = 32
    const extra = new Uint8Array(extraBytes);
    writeUint16LE(extra, 0, adpcm.samplesPerBlock);
    writeUint16LE(extra, 2, numCoef);
    for (let i = 0; i < numCoef; i++) {
        writeInt16LE(extra, 4 + i * 4,     ADAPT_COEFFS_INT16[i][0]);
        writeInt16LE(extra, 4 + i * 4 + 2, ADAPT_COEFFS_INT16[i][1]);
    }

    const totalFrames = samples.length / channels;
    return buildSoundEffectXnb({
        wFormatTag: 2,                              // MS-ADPCM
        channels,
        sampleRate,
        avgBytesPerSec,
        blockAlign: adpcm.blockAlign,
        bitsPerSample: 4,                            // nibbles
        formatExtra: extra,
        audio: adpcm.bytes,
        totalFrames,
        durationMs: Math.round((totalFrames / sampleRate) * 1000),
    });
}

// ─── 7-bit varint + LE helpers (duplicates xnb-writer.ts; kept local
//     so the audio path doesn't depend on the texture writer module) ───

function write7BitInt(out: number[], value: number) {
    let v = value >>> 0;
    while (v >= 0x80) {
        out.push((v & 0x7F) | 0x80);
        v >>>= 7;
    }
    out.push(v & 0x7F);
}

function pushInt16LE(out: number[], value: number) {
    out.push(value & 0xFF, (value >>> 8) & 0xFF);
}

function pushInt32LE(out: number[], value: number) {
    out.push(value & 0xFF, (value >>> 8) & 0xFF, (value >>> 16) & 0xFF, (value >>> 24) & 0xFF);
}

function writeUint32LE(buf: Uint8Array, offset: number, value: number) {
    buf[offset]     = value & 0xFF;
    buf[offset + 1] = (value >>> 8) & 0xFF;
    buf[offset + 2] = (value >>> 16) & 0xFF;
    buf[offset + 3] = (value >>> 24) & 0xFF;
}

function writeUint16LE(buf: Uint8Array, offset: number, value: number) {
    buf[offset] = value & 0xFF;
    buf[offset + 1] = (value >>> 8) & 0xFF;
}

function writeInt16LE(buf: Uint8Array, offset: number, value: number) {
    const v = value & 0xFFFF;
    buf[offset] = v & 0xFF;
    buf[offset + 1] = (v >>> 8) & 0xFF;
}

const utf8 = new TextEncoder();

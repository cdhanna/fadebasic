// MS-ADPCM (Microsoft ADPCM, WAVE_FORMAT_ADPCM, wFormatTag=2) encoder.
//
// Compresses 16-bit PCM to 4-bit nibbles at ~4:1 with quality close to
// PCM for typical game SFX. Each block stores a small header per channel
// (predictor index, initial delta, two seed samples) followed by 4-bit
// difference nibbles. The decoder is built into KNI/MonoGame's
// SoundEffect reader — no runtime code needed on our side.
//
// Block layout — MONO:
//   byte 0:       predictor index (0..6)
//   bytes 1-2:    iDelta            (int16 LE, initial step size)
//   bytes 3-4:    Samp1             (int16 LE, sample at t-1)
//   bytes 5-6:    Samp2             (int16 LE, sample at t-2)
//   bytes 7..end: nibble stream     (each byte = [high nibble][low nibble],
//                                    earlier sample in HIGH nibble)
//
// Block layout — STEREO (interleaved):
//   bytes 0-6:    left channel header (as above)
//   bytes 7-13:   right channel header
//   bytes 14..:   one byte per time-step: HIGH nibble = left, LOW nibble = right
//
// References:
//   https://learn.microsoft.com/en-us/windows/win32/multimedia/microsoft-adpcm
//   https://wiki.multimedia.cx/index.php/Microsoft_ADPCM

// AdaptCoeff[i] = [coeff1, coeff2] used by predictor i.
// The encoder picks the best i per block; the decoder applies the same
// coefficients (which it reads from the format-specific suffix in
// WAVEFORMATEX).
export const ADAPT_COEFFS_INT16: ReadonlyArray<[number, number]> = [
    [256,    0],
    [512, -256],
    [  0,    0],
    [192,   64],
    [240,    0],
    [460, -208],
    [392, -232],
];

// Each emitted nibble (treated as unsigned 0..15) indexes into this
// table to scale iDelta for the next sample. Larger nibbles indicate
// the predictor missed badly, so the step size grows; smaller nibbles
// shrink it. The values are spec-defined.
const ADAPTATION_TABLE: ReadonlyArray<number> = [
    230, 230, 230, 230, 307, 409, 512, 614,
    768, 614, 512, 409, 307, 230, 230, 230,
];

const DELTA_MIN = 16;          // floor for iDelta; the spec says >= 16
const PROBE_SAMPLES = 16;      // window used to score predictor candidates

export interface AdpcmEncodeResult {
    /** Compressed nibble stream (block-aligned). */
    bytes: Uint8Array;
    /** Per-channel total samples per block (same value the WAVEFORMATEX
     *  suffix advertises). */
    samplesPerBlock: number;
    /** Total bytes per block (the WAVEFORMATEX nBlockAlign). */
    blockAlign: number;
    /** Number of complete blocks emitted. */
    blockCount: number;
}

/** Encode interleaved 16-bit PCM into MS-ADPCM blocks.
 *  - `samples`: interleaved Int16 PCM (length = totalFrames * channels)
 *  - `channels`: 1 or 2
 *  - `blockAlign`: bytes per block (caller-chosen; we round up to a
 *    legal value if it's too small for the header to fit). */
export function encodeMsAdpcm(
    samples: Int16Array,
    channels: number,
    blockAlign: number,
): AdpcmEncodeResult {
    if (channels < 1 || channels > 2) {
        throw new Error(`adpcm-encoder: ${channels}-channel input — only mono/stereo supported.`);
    }
    const headerBytes = 7 * channels;
    if (blockAlign < headerBytes + 1) {
        // The block must at least hold both headers + 1 data byte.
        blockAlign = headerBytes + 1;
    }
    // samplesPerBlock — same on both channels because of interleaved
    // layout for stereo:
    //   mono:   2 (header seeds) + 2 * (blockAlign - 7)
    //   stereo: 2 (header seeds) + (blockAlign - 14)
    const samplesPerBlock = channels === 1
        ? 2 + 2 * (blockAlign - 7)
        : 2 + (blockAlign - 14);
    const totalFrames = samples.length / channels;
    const blockCount = Math.ceil(totalFrames / samplesPerBlock);
    const out = new Uint8Array(blockCount * blockAlign);

    // De-interleave into per-channel buffers — easier to encode against
    // than the interleaved input.
    const channelBuf: Int16Array[] = [];
    for (let c = 0; c < channels; c++) channelBuf.push(new Int16Array(totalFrames));
    for (let i = 0; i < totalFrames; i++) {
        for (let c = 0; c < channels; c++) channelBuf[c][i] = samples[i * channels + c];
    }

    for (let block = 0; block < blockCount; block++) {
        const sampleStart = block * samplesPerBlock;
        const sampleEnd = Math.min(sampleStart + samplesPerBlock, totalFrames);
        const blockOffset = block * blockAlign;

        // Channel-private state survives only inside the block.
        const state: ChannelState[] = [];
        for (let c = 0; c < channels; c++) {
            // Pad if this is the last block and the source is short.
            const buf = channelBuf[c];
            const lastValid = buf[sampleEnd - 1] ?? 0;
            const block_ch: Int16Array = new Int16Array(samplesPerBlock);
            for (let i = 0; i < samplesPerBlock; i++) {
                const idx = sampleStart + i;
                block_ch[i] = idx < sampleEnd ? buf[idx] : lastValid;
            }
            const predictor = chooseBestPredictor(block_ch);
            const samp2 = block_ch[0];
            const samp1 = block_ch[1];
            const delta = pickInitialDelta(block_ch);
            // Write the channel's 7-byte header at the right slot.
            const headerOffset = blockOffset + c * 7;
            out[headerOffset] = predictor;
            writeInt16LE(out, headerOffset + 1, delta);
            writeInt16LE(out, headerOffset + 3, samp1);
            writeInt16LE(out, headerOffset + 5, samp2);
            state.push({ predictor, delta, samp1, samp2, samples: block_ch });
        }

        // Data bytes begin after both headers.
        let dataOff = blockOffset + headerBytes;
        if (channels === 1) {
            const s = state[0];
            // 2 nibbles per byte; encode in pairs starting from sample index 2.
            for (let i = 2; i < samplesPerBlock; i += 2) {
                const high = encodeOne(s, s.samples[i]);
                const low = encodeOne(s, s.samples[i + 1]);
                out[dataOff++] = ((high & 0x0F) << 4) | (low & 0x0F);
            }
        } else {
            const L = state[0], R = state[1];
            // One byte per time-step: HIGH = left, LOW = right.
            for (let i = 2; i < samplesPerBlock; i++) {
                const ln = encodeOne(L, L.samples[i]);
                const rn = encodeOne(R, R.samples[i]);
                out[dataOff++] = ((ln & 0x0F) << 4) | (rn & 0x0F);
            }
        }
    }

    return { bytes: out, samplesPerBlock, blockAlign, blockCount };
}

interface ChannelState {
    predictor: number;
    delta: number;
    samp1: number;
    samp2: number;
    samples: Int16Array;
}

/** Encode one sample, advance the channel state, return the 4-bit
 *  unsigned nibble. */
function encodeOne(s: ChannelState, target: number): number {
    const [c1, c2] = ADAPT_COEFFS_INT16[s.predictor];
    // Linear predictor — the spec divides the dot-product by 256 with
    // truncation-toward-zero (standard C integer division). Arithmetic
    // right-shift (`>> 8`) rounds toward negative infinity instead,
    // which biases the predictor downward for negative sums and shows
    // up as audible per-sample noise. `Math.trunc` matches the decoder.
    const sum = s.samp1 * c1 + s.samp2 * c2;
    const predicted = Math.trunc(sum / 256);
    let nibbleSigned = Math.round((target - predicted) / s.delta);
    if (nibbleSigned > 7) nibbleSigned = 7;
    if (nibbleSigned < -8) nibbleSigned = -8;
    let reconstructed = predicted + nibbleSigned * s.delta;
    if (reconstructed >  32767) reconstructed =  32767;
    if (reconstructed < -32768) reconstructed = -32768;
    const nibble = nibbleSigned & 0x0F;
    // Delta update is always positive (table values + delta both > 0),
    // so `>> 8` is safe here and faster than Math.trunc.
    s.delta = Math.max(DELTA_MIN, (ADAPTATION_TABLE[nibble] * s.delta) >> 8);
    s.samp2 = s.samp1;
    s.samp1 = reconstructed;
    return nibble;
}

/** Try each of the 7 predictors against a short window at the start of
 *  the block; pick the one with the lowest squared error after
 *  simulated encoding. Simple but effective — for typical SFX the win
 *  over "always predictor 0" is noticeable on transients. */
function chooseBestPredictor(block: Int16Array): number {
    let bestP = 0;
    let bestErr = Infinity;
    const probeEnd = Math.min(block.length, 2 + PROBE_SAMPLES);
    const initialDelta = pickInitialDelta(block);
    for (let p = 0; p < ADAPT_COEFFS_INT16.length; p++) {
        const sim: ChannelState = {
            predictor: p,
            delta: initialDelta,
            samp1: block[1],
            samp2: block[0],
            samples: block,
        };
        let err = 0;
        for (let i = 2; i < probeEnd; i++) {
            encodeOne(sim, block[i]);  // mutates sim
            const diff = sim.samp1 - block[i];
            err += diff * diff;
        }
        if (err < bestErr) { bestErr = err; bestP = p; }
    }
    return bestP;
}

/** Initial iDelta heuristic — half the average absolute frame-to-frame
 *  delta over the first PROBE_SAMPLES samples, floored at DELTA_MIN. A
 *  reasonable trade-off: aggressive enough to encode quiet passages
 *  well but not so small that loud transients clip. */
function pickInitialDelta(block: Int16Array): number {
    const probeEnd = Math.min(block.length, 2 + PROBE_SAMPLES);
    let sum = 0;
    let count = 0;
    for (let i = 2; i < probeEnd; i++) {
        sum += Math.abs(block[i] - block[i - 1]);
        count++;
    }
    if (count === 0) return DELTA_MIN;
    const avg = sum / count;
    return Math.max(DELTA_MIN, Math.round(avg / 4));
}

function writeInt16LE(buf: Uint8Array, offset: number, value: number) {
    // Two's-complement little-endian write that handles negative ints.
    const v = value & 0xFFFF;
    buf[offset] = v & 0xFF;
    buf[offset + 1] = (v >>> 8) & 0xFF;
}

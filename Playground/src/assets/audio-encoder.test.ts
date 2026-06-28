import { describe, it, expect } from 'vitest';
import { encodeAdpcmSoundEffectXnb, encodePcmSoundEffectXnb } from './audio-encoder';
import { ADAPT_COEFFS_INT16, encodeMsAdpcm } from './adpcm-encoder';
import { classifyXnb } from '../xnb/xnb-reader';

function silence(durationSec: number, sampleRate: number, channels: number) {
    const samples = new Int16Array(Math.round(durationSec * sampleRate) * channels);
    return { samples, sampleRate, channels, duration: durationSec };
}

function readInt32LE(buf: Uint8Array, off: number): number {
    return buf[off] | (buf[off + 1] << 8) | (buf[off + 2] << 16) | (buf[off + 3] << 24);
}
function readUint16LE(buf: Uint8Array, off: number): number {
    return buf[off] | (buf[off + 1] << 8);
}

describe('encodePcmSoundEffectXnb', () => {
    it('produces an XNB classified as SoundEffect by the reader', () => {
        const xnb = encodePcmSoundEffectXnb(silence(0.1, 44100, 1));
        const c = classifyXnb(xnb);
        expect(c.kind).toBe('sound-effect');
        expect(c.rootReader?.shortName).toBe('SoundEffectReader');
    });

    it('writes the standard PCM WAVEFORMATEX (mono 44.1k)', () => {
        const xnb = encodePcmSoundEffectXnb(silence(0.05, 44100, 1));
        const c = classifyXnb(xnb);
        const od = c.objectData!;
        // od starts with int32 headerSize, then the WAVEFORMATEX bytes.
        expect(readInt32LE(od, 0)).toBe(18);              // headerSize
        expect(readUint16LE(od, 4)).toBe(1);              // wFormatTag = PCM
        expect(readUint16LE(od, 6)).toBe(1);              // channels
        expect(readInt32LE(od, 8)).toBe(44100);           // sampleRate
        expect(readInt32LE(od, 12)).toBe(44100 * 2);      // avgBytesPerSec
        expect(readUint16LE(od, 16)).toBe(2);             // blockAlign
        expect(readUint16LE(od, 18)).toBe(16);            // bitsPerSample
        expect(readUint16LE(od, 20)).toBe(0);             // cbSize
    });

    it('writes loopStart/loopLength/durationMs after the audio payload', () => {
        // 0.1s @ 44.1k stereo = 4410 frames × 4 bytes = 17640 bytes audio.
        const xnb = encodePcmSoundEffectXnb(silence(0.1, 44100, 2));
        const c = classifyXnb(xnb);
        const od = c.objectData!;
        const headerSize = readInt32LE(od, 0);
        const dataSizeOff = 4 + headerSize;
        const dataSize = readInt32LE(od, dataSizeOff);
        expect(dataSize).toBe(4410 * 4);

        const loopStartOff = dataSizeOff + 4 + dataSize;
        const loopStart = readInt32LE(od, loopStartOff);
        const loopLength = readInt32LE(od, loopStartOff + 4);
        const durationMs = readInt32LE(od, loopStartOff + 8);
        expect(loopStart).toBe(0);
        expect(loopLength).toBe(4410);
        // 100ms — the SoundEffectReader's third int32; KNI throws
        // EndOfStreamException without it.
        expect(durationMs).toBe(100);
    });

    it('refuses to encode > 2 channels', () => {
        expect(() => encodePcmSoundEffectXnb(silence(0.05, 44100, 4)))
            .toThrowError(/only mono\/stereo/);
    });
});

describe('encodeAdpcmSoundEffectXnb', () => {
    function ramp(durationSec: number, sampleRate: number, channels: number) {
        const total = Math.round(durationSec * sampleRate);
        const samples = new Int16Array(total * channels);
        for (let i = 0; i < total; i++) {
            // Sawtooth that exercises the predictor — pure silence would
            // collapse every encoder into a no-op and miss real bugs.
            const v = (((i * 1234567) | 0) & 0xFFFF) - 0x8000;
            for (let c = 0; c < channels; c++) samples[i * channels + c] = v;
        }
        return { samples, sampleRate, channels, duration: durationSec };
    }

    it('writes wFormatTag=2 + cbSize=32 + the canonical coefficient table', () => {
        const xnb = encodeAdpcmSoundEffectXnb(ramp(0.1, 44100, 1));
        const c = classifyXnb(xnb);
        expect(c.kind).toBe('sound-effect');
        const od = c.objectData!;
        const headerSize = readInt32LE(od, 0);
        expect(headerSize).toBe(18 + 32);                // WAVEFORMATEX + ADPCM extras
        expect(readUint16LE(od, 4)).toBe(2);             // wFormatTag = WAVE_FORMAT_ADPCM
        expect(readUint16LE(od, 18)).toBe(4);            // wBitsPerSample = 4 (nibbles)
        expect(readUint16LE(od, 20)).toBe(32);           // cbSize
        // The cbSize-prefixed extra bytes start at offset 22:
        //   uint16 samplesPerBlock
        //   uint16 numCoef
        //   int16[7][2] aCoef
        expect(readUint16LE(od, 24)).toBe(7);            // numCoef
        for (let i = 0; i < 7; i++) {
            const lo = od[26 + i * 4] | (od[27 + i * 4] << 8);
            const hi = od[28 + i * 4] | (od[29 + i * 4] << 8);
            // Coefficients are signed int16 in the spec; sign-extend.
            const c0 = lo > 0x7FFF ? lo - 0x10000 : lo;
            const c1 = hi > 0x7FFF ? hi - 0x10000 : hi;
            expect(c0).toBe(ADAPT_COEFFS_INT16[i][0]);
            expect(c1).toBe(ADAPT_COEFFS_INT16[i][1]);
        }
    });

    it('produces a smaller payload than PCM for the same source (≈4:1)', () => {
        const src = ramp(0.5, 44100, 1);
        const pcmSize = encodePcmSoundEffectXnb(src).length;
        const adpcmSize = encodeAdpcmSoundEffectXnb(src).length;
        // Generous lower bound — actual ratio is ~3.8× because of headers.
        expect(adpcmSize).toBeLessThan(pcmSize * 0.4);
    });
});

describe('encodeMsAdpcm (block layout)', () => {
    it('emits the expected block count + per-block sample count for mono', () => {
        const samples = new Int16Array(2024); // exactly 2 blocks @ blockAlign=512
        const r = encodeMsAdpcm(samples, 1, 512);
        expect(r.blockAlign).toBe(512);
        expect(r.samplesPerBlock).toBe(2 + 2 * (512 - 7));
        expect(r.blockCount).toBe(2);
        expect(r.bytes.length).toBe(r.blockCount * r.blockAlign);
    });

    it('emits the expected block count + per-block sample count for stereo', () => {
        const samplesPerBlock = 2 + (1024 - 14);
        const totalFrames = samplesPerBlock * 3;
        const samples = new Int16Array(totalFrames * 2);
        const r = encodeMsAdpcm(samples, 2, 1024);
        expect(r.samplesPerBlock).toBe(samplesPerBlock);
        expect(r.blockCount).toBe(3);
        expect(r.bytes.length).toBe(3 * 1024);
    });

    it('uses a legal predictor index in every block header', () => {
        const samples = new Int16Array(4096);
        for (let i = 0; i < samples.length; i++) samples[i] = i & 0x7FFF;
        const r = encodeMsAdpcm(samples, 1, 512);
        for (let b = 0; b < r.blockCount; b++) {
            const predictor = r.bytes[b * 512];
            expect(predictor).toBeGreaterThanOrEqual(0);
            expect(predictor).toBeLessThan(7);
        }
    });
});

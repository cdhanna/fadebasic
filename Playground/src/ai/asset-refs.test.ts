import { describe, it, expect } from 'vitest';
import { extractAssetRefs, checkAssetRefs } from './asset-refs';

describe('extractAssetRefs', () => {
    it('pulls texture / font / sfx references from Fade code', () => {
        const code = `
            texture 1, "Images/Player"
            font 2, "Fonts/Arial"
            clip = reserve sfx clip id(clip)
            load sfx clip clip, "audio/laser"
        `;
        const refs = extractAssetRefs(code);
        expect(refs).toEqual(expect.arrayContaining([
            { category: 'image', name: 'Images/Player', command: 'texture' },
            { category: 'font', name: 'Fonts/Arial', command: 'font' },
            { category: 'audio', name: 'audio/laser', command: 'load sfx clip' },
        ]));
    });

    it('dedupes repeated references', () => {
        const code = 'texture 1, "Ball"\ntexture 2, "Ball"';
        expect(extractAssetRefs(code)).toHaveLength(1);
    });

    it('matches inside a markdown answer too', () => {
        const md = 'Here you go:\n\n```fade\ntexture 1, "Sprites/Ship"\n```\nThat loads it.';
        expect(extractAssetRefs(md)).toEqual([
            { category: 'image', name: 'Sprites/Ship', command: 'texture' },
        ]);
    });

    it('does not match prose without the loader shape', () => {
        expect(extractAssetRefs('The texture of the ball is smooth.')).toEqual([]);
    });
});

describe('checkAssetRefs', () => {
    const files = ['main.fbasic', 'Images/Player.png', 'Fonts/Arial.ttf', 'fade.json'];

    it('classifies present vs missing assets', () => {
        const code = `
            texture 1, "Images/Player"
            texture 2, "Images/Enemy"
            font 3, "Fonts/Arial"
        `;
        const { present, missing } = checkAssetRefs(code, files);
        expect(present.map(r => r.name)).toEqual(expect.arrayContaining(['Images/Player', 'Fonts/Arial']));
        expect(missing.map(r => r.name)).toEqual(['Images/Enemy']);
    });

    it('treats .xnb compiled assets as present for any category', () => {
        const { missing } = checkAssetRefs('texture 1, "Images/Boss"', ['Images/Boss.xnb']);
        expect(missing).toEqual([]);
    });

    it('is case-insensitive', () => {
        const { missing } = checkAssetRefs('texture 1, "images/player"', files);
        expect(missing).toEqual([]);
    });

    it('returns empty when no asset refs present', () => {
        expect(checkAssetRefs('print "hello"', files)).toEqual({ present: [], missing: [] });
    });
});

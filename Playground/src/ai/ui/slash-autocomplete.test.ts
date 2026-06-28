// @vitest-environment jsdom
import { describe, it, expect, vi } from 'vitest';
import { createSlashAutocomplete, type SlashCmdInfo } from './slash-autocomplete';

const CMDS: SlashCmdInfo[] = [
    { name: 'help', description: 'Show help' },
    { name: 'mode', description: 'Edit mode', aliases: ['edits'] },
    { name: 'model', description: 'Model info' },
    { name: 'tools', description: 'List tools' },
    { name: 'connection', description: 'Connection', aliases: ['conn'] },
];

function setup() {
    const input = document.createElement('textarea');
    const popup = document.createElement('div');
    popup.hidden = true;
    document.body.append(input, popup);
    const submit = vi.fn();
    const ac = createSlashAutocomplete({ input, popup, list: () => CMDS, submit });
    const type = (v: string) => { input.value = v; ac.refresh(); };
    const names = () => Array.from(popup.querySelectorAll('.ai-slash-item-name')).map(e => e.textContent);
    const key = (k: string) => {
        const e = new KeyboardEvent('keydown', { key: k, bubbles: true, cancelable: true });
        const consumed = ac.handleKeydown(e);
        return { consumed, e };
    };
    return { input, popup, ac, submit, type, names, key };
}

describe('slash autocomplete', () => {
    it('opens on "/" with all commands, closes on non-slash text', () => {
        const t = setup();
        t.type('/');
        expect(t.ac.isOpen()).toBe(true);
        expect(t.names().length).toBe(CMDS.length);
        t.type('hello');
        expect(t.ac.isOpen()).toBe(false);
    });

    it('filters by prefix and matches aliases', () => {
        const t = setup();
        t.type('/mo');
        expect(t.names()).toEqual(['/mode', '/model']);
        t.type('/conn');           // alias of connection
        expect(t.names()).toEqual(['/connection']);
    });

    it('closes once a space is typed (args phase)', () => {
        const t = setup();
        t.type('/mode');
        expect(t.ac.isOpen()).toBe(true);
        t.type('/mode auto');
        expect(t.ac.isOpen()).toBe(false);
    });

    it('arrow keys move selection; Tab completes', () => {
        const t = setup();
        t.type('/mo');             // [mode, model]
        expect(t.popup.querySelector('[aria-selected="true"] .ai-slash-item-name')?.textContent).toBe('/mode');
        t.key('ArrowDown');
        expect(t.popup.querySelector('[aria-selected="true"] .ai-slash-item-name')?.textContent).toBe('/model');
        const { consumed } = t.key('Tab');
        expect(consumed).toBe(true);
        expect(t.input.value).toBe('/model ');
        expect(t.ac.isOpen()).toBe(false);
    });

    it('Enter completes a partial, but runs an exact match', () => {
        const t = setup();
        t.type('/mo');             // partial → complete top (mode)
        t.key('Enter');
        expect(t.input.value).toBe('/mode ');
        expect(t.submit).not.toHaveBeenCalled();

        t.type('/help');           // exact → submit
        t.key('Enter');
        expect(t.submit).toHaveBeenCalledTimes(1);
    });

    it('Escape closes and does not consume Enter when closed', () => {
        const t = setup();
        t.type('/he');
        t.key('Escape');
        expect(t.ac.isOpen()).toBe(false);
        expect(t.key('Enter').consumed).toBe(false); // host handles send
    });
});

// @vitest-environment jsdom

import { describe, it, expect, beforeEach } from 'vitest';
import { mountDiffApproval, lineDiff, compactDiff } from './diff-approval';

let container: HTMLElement;

beforeEach(() => {
    document.body.innerHTML = '';
    container = document.createElement('div');
    document.body.appendChild(container);
});

describe('lineDiff', () => {
    it('marks identical content as same', () => {
        const d = lineDiff('a\nb\nc', 'a\nb\nc');
        expect(d.map(l => l.type)).toEqual(['same', 'same', 'same']);
    });

    it('detects a single inserted line in the middle', () => {
        const d = lineDiff('a\nb\nc', 'a\nNEW\nb\nc');
        expect(d.map(l => l.type)).toEqual(['same', 'add', 'same', 'same']);
    });

    it('detects a single replaced line', () => {
        const d = lineDiff('a\nold\nc', 'a\nnew\nc');
        expect(d.map(l => l.type)).toEqual(['same', 'remove', 'add', 'same']);
    });

    it('treats empty input as no lines (not [""])', () => {
        // Important: oldText='' is falsy → [] not [''], which keeps the diff
        // for an empty file vs newContent showing all lines as 'add' and
        // avoids a phantom "remove empty line".
        const d = lineDiff('', 'a\nb');
        expect(d.every(l => l.type === 'add')).toBe(true);
        expect(d).toHaveLength(2);
    });
});

describe('compactDiff', () => {
    it('keeps everything when total length is within context * 2', () => {
        const diff = lineDiff('a\nb\nc', 'a\nNEW\nb\nc');
        const rows = compactDiff(diff, 3);
        // 4 rows, all visible — nothing to elide.
        expect(rows).toHaveLength(4);
        expect(rows.every(r => r.type !== 'gap')).toBe(true);
    });

    it('elides a long run of unchanged lines into a single gap row', () => {
        // 50 unchanged lines, 1 change at the bottom. With context=3 we
        // expect to keep ~3 lines before the change + the change + ~0 after,
        // and have one gap row for the rest.
        const oldText = Array.from({ length: 50 }, (_, i) => `line ${i + 1}`).join('\n');
        const newText = oldText + '\nNEW';
        const diff = lineDiff(oldText, newText);
        const rows = compactDiff(diff, 3);

        const gaps = rows.filter(r => r.type === 'gap');
        expect(gaps).toHaveLength(1);
        const gap = gaps[0] as { type: 'gap'; hidden: number };
        // ~46 lines hidden (50 total - 3 context - the trailing change is at the end).
        expect(gap.hidden).toBeGreaterThan(40);

        const changeRows = rows.filter(r => r.type === 'add' || r.type === 'remove');
        expect(changeRows).toHaveLength(1);
    });

    it('keeps two changes that share a single context window together (no tiny gap)', () => {
        // Two changes 4 lines apart. With context=3, their windows overlap
        // by 1 line, so all 5 lines in between are kept.
        const oldText = 'a\nb\nc\nd\ne\nf\ng\nh';
        const newText = 'a\nX\nc\nd\ne\nf\nY\nh';
        const diff = lineDiff(oldText, newText);
        const rows = compactDiff(diff, 3);
        expect(rows.find(r => r.type === 'gap')).toBeUndefined();
    });

    it('does not collapse a single-line unchanged run (no net savings)', () => {
        const diff: Array<{ type: 'add' | 'remove' | 'same'; text: string }> = [
            { type: 'add', text: 'A' },
            { type: 'same', text: 'x' },
            { type: 'add', text: 'B' },
        ];
        // With contextLines=0, the lone 'same' would otherwise be a 1-line
        // gap. We keep it as-is since the gap row would itself take a line.
        const rows = compactDiff(diff, 0);
        expect(rows.find(r => r.type === 'gap')).toBeUndefined();
    });
});

describe('mountDiffApproval — DOM structure (the symptom in the screenshot)', () => {
    it('renders wrapper / header / body / actions / hint in the container', () => {
        mountDiffApproval({
            container,
            path: 'main.fbasic',
            oldContent: 'a\nb\nc',
            newContent: 'a\nNEW\nb\nc',
            onApprove: () => {},
            onReject: () => {},
        });

        const wrapper = container.querySelector('.ai-diff-wrapper');
        expect(wrapper, 'wrapper should be in the container').not.toBeNull();
        expect(wrapper!.querySelector('.ai-diff-header'), 'header').not.toBeNull();
        expect(wrapper!.querySelector('.ai-diff'), 'diff <pre>').not.toBeNull();
        expect(wrapper!.querySelector('.ai-diff-actions'), 'actions row').not.toBeNull();
        expect(wrapper!.querySelector('.ai-diff-apply'), 'apply button').not.toBeNull();
        expect(wrapper!.querySelector('.ai-diff-reject'), 'reject button').not.toBeNull();
        expect(container.querySelector('.ai-approval-hint'), 'hint sibling').not.toBeNull();
    });

    it('summarizes a normal edit in the header as +N -M', () => {
        mountDiffApproval({
            container,
            path: 'main.fbasic',
            oldContent: 'a\nold\nc',
            newContent: 'a\nnew\nc',
            onApprove: () => {},
            onReject: () => {},
        });
        const header = container.querySelector('.ai-diff-header')!.textContent ?? '';
        expect(header).toContain('Edit: main.fbasic');
        expect(header).toContain('+1');
        expect(header).toContain('−1');
    });

    it('summarizes a new file as "Create file: …"', () => {
        mountDiffApproval({
            container,
            path: 'new.fade',
            oldContent: '',
            newContent: 'line 1\nline 2\nline 3',
            onApprove: () => {},
            onReject: () => {},
        });
        const header = container.querySelector('.ai-diff-header')!.textContent ?? '';
        expect(header).toContain('Create file: new.fade');
        expect(header).toContain('3 lines');
    });

    it('renders the empty-changes placeholder (not an invisible empty <pre>) when diff is all-same', () => {
        mountDiffApproval({
            container,
            path: 'noop.fade',
            oldContent: 'a\nb\nc',
            newContent: 'a\nb\nc',
            onApprove: () => {},
            onReject: () => {},
        });
        const empty = container.querySelector('.ai-diff-empty');
        expect(empty, 'placeholder for no-op edits').not.toBeNull();
        expect(empty!.textContent).toContain('matches the current contents');
        // Specifically NOT an empty diff <pre> — that's what caused the
        // "wrapper looks empty" bug in the screenshot.
        expect(container.querySelector('.ai-diff')).toBeNull();
    });

    it('collapses the diff body when the header is clicked', () => {
        mountDiffApproval({
            container,
            path: 'main.fbasic',
            oldContent: 'a\nb\nc',
            newContent: 'a\nNEW\nb\nc',
            onApprove: () => {},
            onReject: () => {},
        });
        const wrapper = container.querySelector<HTMLElement>('.ai-diff-wrapper')!;
        const header = container.querySelector<HTMLButtonElement>('.ai-diff-header')!;
        const body = wrapper.querySelector('.ai-diff-body');
        const actions = wrapper.querySelector('.ai-diff-actions');
        expect(body).not.toBeNull();
        expect(actions).not.toBeNull();
        expect(wrapper.classList.contains('ai-diff-collapsed')).toBe(false);

        header.click();
        expect(wrapper.classList.contains('ai-diff-collapsed')).toBe(true);
        expect(actions).not.toBeNull();

        header.click();
        expect(wrapper.classList.contains('ai-diff-collapsed')).toBe(false);
    });
});

describe('mountDiffApproval — button behavior', () => {
    it('fires onApprove and disables the buttons on Apply', () => {
        let approved = 0, rejected = 0;
        mountDiffApproval({
            container,
            path: 'a.fade',
            oldContent: 'a',
            newContent: 'b',
            onApprove: () => { approved++; },
            onReject: () => { rejected++; },
        });
        const apply = container.querySelector<HTMLButtonElement>('.ai-diff-apply')!;
        const reject = container.querySelector<HTMLButtonElement>('.ai-diff-reject')!;
        apply.click();
        expect(approved).toBe(1);
        expect(rejected).toBe(0);
        expect(apply.disabled).toBe(true);
        expect(reject.disabled).toBe(true);
        // Marks the wrapper for styling
        expect(container.querySelector('.ai-diff-wrapper')!.classList.contains('ai-diff-accepted')).toBe(true);
    });

    it('fires onReject and disables the buttons on Reject', () => {
        let approved = 0, rejected = 0;
        mountDiffApproval({
            container,
            path: 'a.fade',
            oldContent: 'a',
            newContent: 'b',
            onApprove: () => { approved++; },
            onReject: () => { rejected++; },
        });
        const reject = container.querySelector<HTMLButtonElement>('.ai-diff-reject')!;
        reject.click();
        expect(rejected).toBe(1);
        expect(approved).toBe(0);
        expect(container.querySelector('.ai-diff-wrapper')!.classList.contains('ai-diff-rejected')).toBe(true);
    });

    it('only fires the callback once even if the user clicks both buttons quickly', () => {
        // The disabled flag should prevent double-fires, but the close()
        // path also guards `resolved`. Test both behaviors.
        let approved = 0, rejected = 0;
        mountDiffApproval({
            container,
            path: 'a.fade',
            oldContent: 'a',
            newContent: 'b',
            onApprove: () => { approved++; },
            onReject: () => { rejected++; },
        });
        const apply = container.querySelector<HTMLButtonElement>('.ai-diff-apply')!;
        apply.click();
        apply.click();
        apply.click();
        expect(approved).toBe(1);
        expect(rejected).toBe(0);
    });
});

describe('mountDiffApproval — container-relative sizing', () => {
    it('caps wrapper max-height to the container height when known', () => {
        // jsdom doesn't compute layout, so stub the rect. This is the
        // pinch point: a chat panel that's been resized to ~280px tall
        // must NOT host a wrapper taller than itself — that's what
        // caused the "blue box around only the buttons" symptom.
        container.getBoundingClientRect = () => ({
            x: 0, y: 0, width: 400, height: 280, top: 0, left: 0,
            right: 400, bottom: 280, toJSON() { return this; },
        }) as DOMRect;

        mountDiffApproval({
            container,
            path: 'main.fbasic',
            oldContent: 'a\nb',
            newContent: 'a\nNEW\nb',
            onApprove: () => {},
            onReject: () => {},
        });
        const wrapper = container.querySelector<HTMLElement>('.ai-diff-wrapper')!;
        expect(wrapper.style.maxHeight).toBe('256px'); // 280 - 24 breathing room
    });

    it('skips the inline cap when the container has no height yet', () => {
        // Default jsdom rect is all-zero. Falls back to CSS max-height.
        mountDiffApproval({
            container,
            path: 'main.fbasic',
            oldContent: 'a\nb',
            newContent: 'a\nNEW\nb',
            onApprove: () => {},
            onReject: () => {},
        });
        const wrapper = container.querySelector<HTMLElement>('.ai-diff-wrapper')!;
        expect(wrapper.style.maxHeight).toBe('');
    });

    it('sets flex-shrink: 0 inline so a flex-column parent cannot collapse it', () => {
        // Real failure mode: `.ai-messages` is a flex column. With
        // overflow: hidden on the wrapper, `min-height: auto` resolves to 0
        // and flex-shrink defaults to 1, so sibling tool rows above can
        // squeeze the wrapper down to a thin blue line. We pin
        // flex-shrink to 0 inline as belt-and-suspenders alongside the
        // CSS rule so the standalone demo and any other host are also
        // protected.
        mountDiffApproval({
            container,
            path: 'main.fbasic',
            oldContent: 'a',
            newContent: 'b',
            onApprove: () => {},
            onReject: () => {},
        });
        const wrapper = container.querySelector<HTMLElement>('.ai-diff-wrapper')!;
        expect(wrapper.style.flexShrink).toBe('0');
    });
});

describe('mountDiffApproval — forceReject()', () => {
    it('rejects the pending dialog when called from outside (Stop button path)', () => {
        let approved = 0, rejected = 0;
        const handle = mountDiffApproval({
            container,
            path: 'a.fade',
            oldContent: 'a',
            newContent: 'b',
            onApprove: () => { approved++; },
            onReject: () => { rejected++; },
        });
        handle.forceReject();
        expect(rejected).toBe(1);
        expect(approved).toBe(0);
    });

    it('is idempotent — calling forceReject after the user clicked is a no-op', () => {
        let approved = 0, rejected = 0;
        const handle = mountDiffApproval({
            container,
            path: 'a.fade',
            oldContent: 'a',
            newContent: 'b',
            onApprove: () => { approved++; },
            onReject: () => { rejected++; },
        });
        container.querySelector<HTMLButtonElement>('.ai-diff-apply')!.click();
        handle.forceReject();
        expect(approved).toBe(1);
        expect(rejected).toBe(0);
    });
});

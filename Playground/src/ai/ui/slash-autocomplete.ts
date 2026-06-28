// Slash-command autocomplete for the chat input. Extracted from ai-chat so
// it can be unit-tested in jsdom without mounting the whole panel (which
// dockview lazy-mounts, making it awkward to drive from an e2e probe).

export interface SlashCmdInfo {
    name: string;
    description: string;
    aliases?: string[];
}

export interface SlashAutocomplete {
    /** Recompute the popup from the current input value. Call on 'input'. */
    refresh(): void;
    /** Handle a keydown while the popup may be open. Returns true if the
     *  event was consumed (caller should not also treat it as send/newline). */
    handleKeydown(e: KeyboardEvent): boolean;
    close(): void;
    isOpen(): boolean;
}

export function createSlashAutocomplete(opts: {
    input: HTMLTextAreaElement;
    popup: HTMLElement;
    list: () => SlashCmdInfo[];
    /** Send the current input (used when Enter is pressed on an exact match). */
    submit: () => void;
}): SlashAutocomplete {
    const { input, popup } = opts;
    let items: SlashCmdInfo[] = [];
    let sel = 0;

    const isOpen = () => !popup.hidden;

    function close(): void {
        popup.hidden = true;
        popup.innerHTML = '';
        items = [];
    }

    function render(): void {
        popup.innerHTML = '';
        items.forEach((cmd, i) => {
            const item = document.createElement('div');
            item.className = 'ai-slash-item';
            item.setAttribute('role', 'option');
            item.setAttribute('aria-selected', i === sel ? 'true' : 'false');
            const name = document.createElement('span');
            name.className = 'ai-slash-item-name';
            name.textContent = `/${cmd.name}`;
            const desc = document.createElement('span');
            desc.className = 'ai-slash-item-desc';
            desc.textContent = cmd.description;
            item.append(name, desc);
            // mousedown (not click) so it fires before the input's blur.
            item.addEventListener('mousedown', (e) => { e.preventDefault(); accept(cmd); });
            popup.appendChild(item);
        });
    }

    function accept(cmd: SlashCmdInfo): void {
        input.value = `/${cmd.name} `;
        close();
        input.focus();
        // Let the host re-run autosize / refresh (which will keep us closed,
        // since "/name " has a space and no longer matches).
        input.dispatchEvent(new Event('input', { bubbles: true }));
    }

    function move(delta: number): void {
        if (items.length === 0) return;
        sel = (sel + delta + items.length) % items.length;
        render();
        (popup.children[sel] as HTMLElement | undefined)?.scrollIntoView?.({ block: 'nearest' });
    }

    function refresh(): void {
        // Only while typing the command word: a leading "/" and no space yet.
        const m = /^\/(\S*)$/.exec(input.value);
        if (!m) { close(); return; }
        const partial = m[1].toLowerCase();
        const matches = opts.list().filter(c =>
            c.name.startsWith(partial)
            || (c.aliases ?? []).some(a => a.startsWith(partial))
            || c.name.includes(partial));
        if (matches.length === 0) { close(); return; }
        items = matches;
        sel = 0;
        popup.hidden = false;
        render();
    }

    function handleKeydown(e: KeyboardEvent): boolean {
        if (!isOpen()) return false;
        if (e.key === 'ArrowDown') { e.preventDefault(); move(1); return true; }
        if (e.key === 'ArrowUp') { e.preventDefault(); move(-1); return true; }
        if (e.key === 'Escape') { e.preventDefault(); close(); return true; }
        if (e.key === 'Tab' && items[sel]) { e.preventDefault(); accept(items[sel]); return true; }
        if (e.key === 'Enter' && !e.shiftKey && items[sel]) {
            e.preventDefault();
            // Full command name typed → run it; partial → complete it.
            const typed = input.value.slice(1).toLowerCase();
            const exact = items.some(c => c.name === typed || (c.aliases ?? []).includes(typed));
            if (exact) { close(); opts.submit(); }
            else accept(items[sel]);
            return true;
        }
        return false;
    }

    return { refresh, handleKeydown, close, isOpen };
}

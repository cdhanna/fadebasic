// SlashCommandRegistry — maps names + aliases to commands, parses user
// input, and dispatches. Lifecycle is process-wide (one registry per chat
// panel; commands are stateless so sharing is fine).

import type { SlashCommand, SlashContext, SlashResult, SlashStateSnapshot } from './types';

export class SlashCommandRegistry {
    private commands = new Map<string, SlashCommand>();
    /** Alias → canonical name. Lookup goes through here so /h finds /help. */
    private aliases = new Map<string, string>();

    register(cmd: SlashCommand): void {
        this.commands.set(cmd.name, cmd);
        for (const a of cmd.aliases ?? []) this.aliases.set(a, cmd.name);
    }

    get(name: string): SlashCommand | undefined {
        return this.commands.get(name) ?? this.commands.get(this.aliases.get(name) ?? '');
    }

    list(): SlashCommand[] {
        // Sort alphabetically — stable display in /help.
        return [...this.commands.values()].sort((a, b) => a.name.localeCompare(b.name));
    }

    /** Parse a chat input. Returns null when the input doesn't look like a
     *  slash command (so the caller can fall through to a normal send). */
    static parse(input: string): { name: string; args: string } | null {
        const trimmed = input.trim();
        if (!trimmed.startsWith('/')) return null;
        // A bare "/" by itself isn't a command — let it through as user
        // text (the model can decide what to do).
        const body = trimmed.slice(1);
        if (body.length === 0) return null;
        const spaceIdx = body.search(/\s/);
        if (spaceIdx < 0) return { name: body.toLowerCase(), args: '' };
        return {
            name: body.slice(0, spaceIdx).toLowerCase(),
            args: body.slice(spaceIdx + 1).trim(),
        };
    }

    /** Run a parsed command. Returns the SlashResult to render, or a
     *  synthesized "unknown command" error when nothing matched. */
    async run(
        input: string,
        ctxBuilder: (lookup: (name: string) => SlashCommand | undefined, list: () => SlashCommand[]) => Omit<SlashContext, 'lookup' | 'list'>,
    ): Promise<SlashResult | null> {
        const parsed = SlashCommandRegistry.parse(input);
        if (!parsed) return null;

        const cmd = this.get(parsed.name);
        if (!cmd) {
            const available = this.list().map(c => `/${c.name}`).join(', ');
            return {
                title: `Unknown command: /${parsed.name}`,
                body: `Try /help. Available: ${available}`,
                variant: 'error',
            };
        }

        const ctx: SlashContext = {
            ...ctxBuilder(n => this.get(n), () => this.list()),
            lookup: n => this.get(n),
            list: () => this.list(),
        };

        try {
            return await cmd.execute(parsed.args, ctx);
        } catch (e) {
            return {
                title: `Error in /${parsed.name}`,
                body: (e as Error).message ?? String(e),
                variant: 'error',
            };
        }
    }
}

/** Helper for tests + clients: build an empty SlashStateSnapshot. */
export function emptySlashState(): SlashStateSnapshot {
    return { lastDocs: null, lastPlan: null, recentEvents: [] };
}

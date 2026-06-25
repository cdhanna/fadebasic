// Slash commands — client-side introspection / control commands the user
// types into the chat input. Unlike tools, they NEVER reach the LLM.
// Each one is a deterministic render of state the chat panel already
// tracks. Adding one is a single file + a registry entry.
//
// Conventions:
//   - Names are lowercase, no spaces ("/clear", "/tools").
//   - Arguments come after the name as a single string ("/search docs how
//     do sprites rotate?"). Command implementations parse them however
//     they like.
//   - Output is rendered inline as a special "system info" bubble — visually
//     distinct from chat so the user knows it's not the model talking.

import type { Agent, AgentEvent } from '../agent';
import type { GrammarAgent } from '../loop/grammar-agent';
import type { ChatProvider } from '../providers/types';
import type { ToolRegistry } from '../tools';
import type { AgentPlan } from '../tool-protocol';
import type { SearchHit } from '../rag/types';

/** Snapshot of recent agent state — fed into slash commands that need to
 *  inspect "what just happened" rather than "what's true forever". */
export interface SlashStateSnapshot {
    /** Hits from the most recent docs_retrieved event, or null. */
    lastDocs: SearchHit[] | null;
    /** Most recent plan emitted in this conversation, or null. */
    lastPlan: AgentPlan | null;
    /** All AgentEvents observed this session (cap to last 200 for /context). */
    recentEvents: AgentEvent[];
}

/** Context handed to every command. Implementations can lazily read what
 *  they need; not every command uses everything. agent + provider may be
 *  null before a model is loaded — /help and /tools still work. */
export interface SlashContext {
    agent: Agent | GrammarAgent | null;
    provider: ChatProvider | null;
    tools: ToolRegistry;
    state: SlashStateSnapshot;
    /** Side-effect callbacks the panel provides. May be undefined if the
     *  hosting page doesn't support them. */
    callbacks: {
        clearConversation?: () => void;
        focusLogs?: (channelPattern: RegExp) => void;
        /** Read/set how code edits are applied: 'manual' (review each diff)
         *  or 'auto' (apply immediately). Used by /mode. */
        getEditMode?: () => 'manual' | 'auto';
        setEditMode?: (m: 'manual' | 'auto') => void;
        /** A human-readable summary of the current connection (provider,
         *  GhostBot code/status/model). Used by /connection. */
        getConnectionInfo?: () => string;
    };
    /** Look up another command by name — used by `/help` to introspect
     *  itself + by aliases. */
    lookup(name: string): SlashCommand | undefined;
    list(): SlashCommand[];
}

export interface SlashCommand {
    /** Name without the leading slash. */
    name: string;
    /** One-line description, shown in /help. */
    description: string;
    /** Aliases — alternate names that route to the same handler. */
    aliases?: string[];
    /** Execute the command. Returns a SlashResult to render, or null to
     *  indicate the command produced no output (e.g. /clear). */
    execute(args: string, ctx: SlashContext): SlashResult | null | Promise<SlashResult | null>;
}

/** The rendered output of a slash command. Body can be plain text (with
 *  newlines preserved) or a fully-built HTMLElement for richer layouts. */
export interface SlashResult {
    title: string;
    body: string | HTMLElement;
    /** Visual variant for the bubble. Default 'info'. */
    variant?: 'info' | 'error';
}

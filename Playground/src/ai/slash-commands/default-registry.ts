import { SlashCommandRegistry } from './registry';
import { help } from './help';
import { tools } from './tools';
import { model } from './model';
import { context } from './context';
import { plan } from './plan';
import { clear } from './clear';
import { logs } from './logs';
import { mode } from './mode';
import { connection } from './connection';

export function createDefaultSlashRegistry(): SlashCommandRegistry {
    const r = new SlashCommandRegistry();
    r.register(help);
    r.register(tools);
    r.register(model);
    r.register(mode);
    r.register(connection);
    r.register(context);
    r.register(plan);
    r.register(clear);
    r.register(logs);
    return r;
}

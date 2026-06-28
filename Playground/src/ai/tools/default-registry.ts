// Default set of tools shipped with the playground. New tools are registered
// here so the agent loop picks them up automatically. Tools that depend on
// optional adapters (editor, diagnostics) are still registered — they
// degrade gracefully when the adapter is absent.

import { ToolRegistry } from './index';
import { listFiles } from './list-files';
import { readFile } from './read-file';
import { applyEdit } from './apply-edit';
import { createFile } from './create-file';
import { searchDocs } from './search-docs';
import { getDiagnostics } from './get-diagnostics';
import { catalogSearch } from './catalog-search';
import { catalogBrowse } from './catalog-browse';
import { catalogImport } from './catalog-import';

export function createDefaultRegistry(): ToolRegistry {
    const r = new ToolRegistry();
    r.register(searchDocs);
    r.register(getDiagnostics);
    r.register(listFiles);
    r.register(readFile);
    r.register(applyEdit);
    r.register(createFile);
    r.register(catalogSearch);
    r.register(catalogBrowse);
    r.register(catalogImport);
    return r;
}

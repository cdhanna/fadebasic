#!/usr/bin/env node
/**
 * Validates Tauri bundle icons decode to width×height RGBA pixels.
 * Catches truncated/corrupt PNGs that panic Tauri at startup:
 *   "invalid icon: dimensions (32x32) don't match the number of pixels"
 */
import { readFileSync, existsSync } from 'node:fs';
import { dirname, join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';

const ROOT = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const TAURI_DIR = join(ROOT, 'src-tauri');
const CONF_PATH = join(TAURI_DIR, 'tauri.conf.json');

function loadIconPaths() {
    const conf = JSON.parse(readFileSync(CONF_PATH, 'utf8'));
    const rel = conf?.bundle?.icon;
    if (!Array.isArray(rel) || rel.length === 0) {
        throw new Error('tauri.conf.json bundle.icon must list at least one PNG');
    }
    return rel.map(p => join(TAURI_DIR, p));
}

const PY = String.raw`
import json, sys
from pathlib import Path
try:
    from PIL import Image
except ImportError:
    print('PIL not available — install Pillow or use Python with PIL', file=sys.stderr)
    sys.exit(2)

errors = []
for raw in json.loads(sys.argv[1]):
    path = Path(raw)
    if not path.is_file():
        errors.append(f'missing icon: {path}')
        continue
    try:
        with Image.open(path) as im:
            w, h = im.size
            if w != h:
                errors.append(f'{path}: icon must be square, got {w}x{h}')
            if w < 32:
                errors.append(f'{path}: icon too small ({w}x{h}), need at least 32x32')
            rgba = im.convert('RGBA')
            rgba.load()
            pixels = w * h
            data = rgba.tobytes()
            expected = pixels * 4
            if len(data) != expected:
                errors.append(
                    f'{path}: expected {expected} bytes of RGBA data for {w}x{h}, got {len(data)}'
                )
    except OSError as e:
        errors.append(f'{path}: broken PNG — {e}')

if errors:
    for e in errors:
        print(e, file=sys.stderr)
    sys.exit(1)
print(f'validated {len(json.loads(sys.argv[1]))} icon(s)')
`;

const iconPaths = loadIconPaths();
for (const p of iconPaths) {
    if (!existsSync(p)) {
        console.error(`missing icon: ${p}`);
        process.exit(1);
    }
}

const result = spawnSync(
    'python3',
    ['-c', PY, JSON.stringify(iconPaths)],
    { encoding: 'utf8', stdio: ['ignore', 'pipe', 'pipe'] },
);

if (result.status !== 0) {
    process.stderr.write(result.stderr || '');
    process.stderr.write(result.stdout || '');
    process.exit(result.status ?? 1);
}
process.stdout.write(result.stdout);

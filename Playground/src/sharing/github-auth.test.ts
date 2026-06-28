// github-auth.ts unit tests — fetch and sleep are both injected so polling
// runs synchronously without burning real wall time. We assert the protocol
// (correct URLs, correct bodies, correct backoff on slow_down, correct error
// translations) and the happy path returning an access token.

import { describe, expect, it } from 'vitest';
import {
    DeviceFlowError,
    pollForToken,
    requestDeviceCode,
    signInWithDeviceFlow,
    validateToken,
} from './github-auth';

const CLIENT_ID = 'Iv23liTestClient';
// Tests pin their own endpoint URLs so they don't depend on the
// production proxy in github-auth-config. Every call site that
// reaches the network passes `deviceCodeUrl` / `tokenUrl` explicitly.
const DEVICE_CODE_URL = 'https://test.example/login/device/code';
const TOKEN_URL = 'https://test.example/login/oauth/access_token';

function jsonResp(body: unknown, status = 200, headers: Record<string, string> = {}): Response {
    return new Response(JSON.stringify(body), {
        status,
        headers: { 'Content-Type': 'application/json', ...headers },
    });
}

// A tiny scriptable fetch mock: each call shifts the next handler off a queue,
// or — if a handler is registered for a URL prefix — returns that. Lets tests
// program "first poll: pending, second poll: ok" without regex juggling.
interface Recorded { url: string; method: string; body: unknown }

class Scripted {
    public requests: Recorded[] = [];
    private next: Array<(req: Recorded) => Response | Promise<Response>> = [];
    private byUrl = new Map<string, (req: Recorded) => Response | Promise<Response>>();

    onNext(respond: (req: Recorded) => Response | Promise<Response>): this {
        this.next.push(respond);
        return this;
    }
    onUrl(url: string, respond: (req: Recorded) => Response | Promise<Response>): this {
        this.byUrl.set(url, respond);
        return this;
    }
    asFetch(): typeof fetch {
        return (async (input: RequestInfo | URL, init: RequestInit = {}) => {
            const url = typeof input === 'string' ? input : input instanceof URL ? input.toString() : input.url;
            const req: Recorded = { url, method: (init.method ?? 'GET').toUpperCase(), body: init.body };
            this.requests.push(req);
            const byUrl = this.byUrl.get(url);
            if (byUrl) return byUrl(req);
            const queued = this.next.shift();
            if (queued) return queued(req);
            return new Response(`unmocked: ${req.method} ${url}`, { status: 599 });
        }) as typeof fetch;
    }
}

// Test sleep that never sleeps but tracks how long was "asked for".
function makeFakeSleep() {
    const calls: number[] = [];
    return {
        sleep: (ms: number) => { calls.push(ms); return Promise.resolve(); },
        calls,
    };
}

// ─── requestDeviceCode ──────────────────────────────────────────────────────

describe('requestDeviceCode', () => {
    it('POSTs client_id + scope and maps the response fields', async () => {
        const fm = new Scripted();
        fm.onUrl(DEVICE_CODE_URL, (req) => {
            expect(req.method).toBe('POST');
            const body = JSON.parse(req.body as string);
            expect(body).toEqual({ client_id: CLIENT_ID, scope: 'repo' });
            return jsonResp({
                device_code: 'dev-1234',
                user_code: 'WDJB-MJHT',
                verification_uri: 'https://github.com/login/device',
                verification_uri_complete: 'https://github.com/login/device?user_code=WDJB-MJHT',
                expires_in: 900,
                interval: 5,
            });
        });
        const prompt = await requestDeviceCode({
            clientId: CLIENT_ID,
            scope: 'repo',
            fetchImpl: fm.asFetch(),
            deviceCodeUrl: DEVICE_CODE_URL,
        });
        expect(prompt).toEqual({
            userCode: 'WDJB-MJHT',
            verificationUri: 'https://github.com/login/device',
            verificationUriComplete: 'https://github.com/login/device?user_code=WDJB-MJHT',
            deviceCode: 'dev-1234',
            expiresIn: 900,
            interval: 5,
        });
    });

    it('omits scope when not provided (GitHub Apps ignore it; permissions are App-level)', async () => {
        const fm = new Scripted();
        let observed: unknown;
        fm.onUrl(DEVICE_CODE_URL, (req) => {
            observed = JSON.parse(req.body as string);
            return jsonResp({ device_code: 'd', user_code: 'u', verification_uri: 'v', expires_in: 0, interval: 5 });
        });
        await requestDeviceCode({
            clientId: CLIENT_ID,
            fetchImpl: fm.asFetch(),
            deviceCodeUrl: DEVICE_CODE_URL,
        });
        expect(observed).toEqual({ client_id: CLIENT_ID });
    });
});

// ─── pollForToken ───────────────────────────────────────────────────────────

describe('pollForToken', () => {
    it('returns the access token on a success response', async () => {
        const fm = new Scripted();
        fm.onUrl(TOKEN_URL, () =>
            jsonResp({
                access_token: 'ghu_xyz',
                token_type: 'bearer',
                scope: 'repo',
                expires_in: 28800,
                refresh_token: 'ghr_abc',
                refresh_token_expires_in: 15_724_800,
            }));
        const { sleep, calls } = makeFakeSleep();
        const t = await pollForToken({
            clientId: CLIENT_ID, deviceCode: 'd', interval: 5,
            fetchImpl: fm.asFetch(), sleepImpl: sleep,
            tokenUrl: TOKEN_URL,
        });
        // Returns the full TokenSet now, not just the access token.
        expect(t.accessToken).toBe('ghu_xyz');
        expect(t.refreshToken).toBe('ghr_abc');
        expect(t.expiresIn).toBe(28800);
        expect(t.refreshTokenExpiresIn).toBe(15_724_800);
        expect(t.scope).toBe('repo');
        expect(t.tokenType).toBe('bearer');
        // Sleeps once (before the first poll) at interval * 1000.
        expect(calls).toEqual([5000]);
    });

    it('keeps polling on authorization_pending and eventually succeeds', async () => {
        const fm = new Scripted();
        fm.onNext(() => jsonResp({ error: 'authorization_pending' }));
        fm.onNext(() => jsonResp({ error: 'authorization_pending' }));
        fm.onNext(() => jsonResp({ access_token: 'ghu_finally' }));
        const { sleep, calls } = makeFakeSleep();
        const t = await pollForToken({
            clientId: CLIENT_ID, deviceCode: 'd', interval: 5,
            fetchImpl: fm.asFetch(), sleepImpl: sleep,
            tokenUrl: TOKEN_URL,
        });
        expect(t.accessToken).toBe('ghu_finally');
        expect(calls).toEqual([5000, 5000, 5000]); // three sleeps, three polls
    });

    it('backs off by +5s when the server says slow_down', async () => {
        const fm = new Scripted();
        fm.onNext(() => jsonResp({ error: 'slow_down' }));
        fm.onNext(() => jsonResp({ error: 'slow_down' }));
        fm.onNext(() => jsonResp({ access_token: 'ok' }));
        const { sleep, calls } = makeFakeSleep();
        await pollForToken({
            clientId: CLIENT_ID, deviceCode: 'd', interval: 5,
            fetchImpl: fm.asFetch(), sleepImpl: sleep,
            tokenUrl: TOKEN_URL,
        });
        // initial=5, +5 after first slow_down=10, +5 after second=15
        expect(calls).toEqual([5000, 10000, 15000]);
    });

    it('throws DeviceFlowError(expired_token)', async () => {
        const fm = new Scripted();
        fm.onNext(() => jsonResp({ error: 'expired_token' }));
        const err = await pollForToken({
            clientId: CLIENT_ID, deviceCode: 'd', interval: 1,
            fetchImpl: fm.asFetch(), sleepImpl: makeFakeSleep().sleep,
            tokenUrl: TOKEN_URL,
        }).catch((e) => e);
        expect(err).toBeInstanceOf(DeviceFlowError);
        expect((err as DeviceFlowError).code).toBe('expired_token');
    });

    it('throws DeviceFlowError(access_denied) when the user denies', async () => {
        const fm = new Scripted();
        fm.onNext(() => jsonResp({ error: 'access_denied' }));
        const err = await pollForToken({
            clientId: CLIENT_ID, deviceCode: 'd', interval: 1,
            fetchImpl: fm.asFetch(), sleepImpl: makeFakeSleep().sleep,
            tokenUrl: TOKEN_URL,
        }).catch((e) => e);
        expect(err).toBeInstanceOf(DeviceFlowError);
        expect((err as DeviceFlowError).code).toBe('access_denied');
    });

    it('throws DeviceFlowError(unsupported_grant_type) when the App lacks device-flow enablement', async () => {
        const fm = new Scripted();
        fm.onNext(() => jsonResp({ error: 'unsupported_grant_type' }));
        const err = await pollForToken({
            clientId: CLIENT_ID, deviceCode: 'd', interval: 1,
            fetchImpl: fm.asFetch(), sleepImpl: makeFakeSleep().sleep,
            tokenUrl: TOKEN_URL,
        }).catch((e) => e);
        expect(err).toBeInstanceOf(DeviceFlowError);
        expect((err as DeviceFlowError).code).toBe('unsupported_grant_type');
        // Message should hint at the actual fix (the App setting).
        expect((err as DeviceFlowError).message).toMatch(/device flow/i);
    });

    it('respects an AbortSignal between polls', async () => {
        const fm = new Scripted();
        fm.onNext(() => jsonResp({ error: 'authorization_pending' }));
        const controller = new AbortController();
        const { sleep } = makeFakeSleep();
        // Wrap sleep so it aborts mid-flight on the second iteration.
        let n = 0;
        const racingSleep = async (ms: number) => {
            await sleep(ms);
            if (++n === 1) controller.abort(new Error('user-canceled'));
        };
        const err = await pollForToken({
            clientId: CLIENT_ID, deviceCode: 'd', interval: 1,
            fetchImpl: fm.asFetch(), sleepImpl: racingSleep, signal: controller.signal,
            tokenUrl: TOKEN_URL,
        }).catch((e) => e);
        expect((err as Error).message).toBe('user-canceled');
    });
});

// ─── signInWithDeviceFlow (the one-call helper) ─────────────────────────────

describe('signInWithDeviceFlow', () => {
    it('fires onPrompt with the device code, then resolves with the TokenSet', async () => {
        const fm = new Scripted();
        fm.onUrl(DEVICE_CODE_URL, () => jsonResp({
            device_code: 'd', user_code: 'AAAA-BBBB',
            verification_uri: 'https://github.com/login/device',
            expires_in: 900, interval: 5,
        }));
        fm.onUrl(TOKEN_URL, () => jsonResp({ access_token: 'ghu_combined' }));
        let observedPrompt: { userCode: string; verificationUri: string } | null = null;
        const result = await signInWithDeviceFlow({
            clientId: CLIENT_ID,
            fetchImpl: fm.asFetch(),
            sleepImpl: makeFakeSleep().sleep,
            deviceCodeUrl: DEVICE_CODE_URL,
            tokenUrl: TOKEN_URL,
            onPrompt: (p) => { observedPrompt = p; },
        });
        expect(result.accessToken).toBe('ghu_combined');
        // Cast through unknown to bypass TS's "always null" inference for
        // closure-assigned vars; we know the callback ran.
        const got = observedPrompt as unknown as { userCode: string; verificationUri: string } | null;
        expect(got?.userCode).toBe('AAAA-BBBB');
        expect(got?.verificationUri).toBe('https://github.com/login/device');
    });
});

// ─── validateToken ──────────────────────────────────────────────────────────

describe('validateToken', () => {
    it('returns login + id + scopes from /user on success', async () => {
        const fm = new Scripted();
        fm.onUrl('https://api.github.com/user', () => {
            // Token must be sent as a bearer header (matches the adapter convention).
            return jsonResp({ login: 'alice', id: 42 }, 200, {
                'X-OAuth-Scopes': 'repo, read:user',
            });
        });
        const out = await validateToken('ghu_token', fm.asFetch());
        expect(out).toEqual({ login: 'alice', id: 42, scopes: ['repo', 'read:user'] });
    });

    it('throws on 401', async () => {
        const fm = new Scripted();
        fm.onUrl('https://api.github.com/user', () => jsonResp({ message: 'Bad credentials' }, 401));
        await expect(validateToken('nope', fm.asFetch())).rejects.toThrow(/401/);
    });

    it('returns an empty scopes array when the header is missing (e.g. fine-grained PATs)', async () => {
        const fm = new Scripted();
        fm.onUrl('https://api.github.com/user', () => jsonResp({ login: 'alice', id: 1 }));
        const out = await validateToken('ghu_fine', fm.asFetch());
        expect(out.scopes).toEqual([]);
    });
});

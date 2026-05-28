// GitHub auth — deployment-specific constants.
//
// Two values: the OAuth proxy worker URL and the GitHub App client ID.
// Both are public information (the client_id is shown to every user
// during the device-flow sign-in; the worker URL is a static target
// for browser fetches), so checking them into the repo is fine.
//
// To redeploy with different values: edit here, rebuild. There's no
// runtime override — keeping the auth surface trivially auditable is
// worth more than the configurability of env vars.

/** Stateless CORS proxy in front of GitHub's device-flow endpoints.
 *  See ../../../oauth-proxy/ for the worker source. The proxy only
 *  relays `/login/device/code` and `/login/oauth/access_token`; it
 *  holds no credentials and stores no state. */
export const OAUTH_PROXY_BASE_URL = 'https://fade-oauth-proxy.cdhannaphone.workers.dev';

/** Client ID for the OAuth/GitHub App backing the device flow. Public
 *  — anyone signing in sees this in the device-flow request. Found
 *  on the App's settings page in GitHub. The prefix tells you which
 *  kind it is:
 *    - `Ov23…` → OAuth App (requires `GITHUB_OAUTH_SCOPE` below)
 *    - `Iv23…` → GitHub App (scope is ignored; permissions are
 *      configured on the App itself) */
export const GITHUB_APP_CLIENT_ID = 'Ov23libpUHDbA7vmFwgH';

/** OAuth scope requested at sign-in. Empty string for GitHub Apps
 *  (their permissions live on the App, not the token). For OAuth
 *  Apps, `repo` is the minimum the Collaboration features need —
 *  it's what `gh auth login` requests by default for repo access.
 *  Without it, the token can read `/user` (so sign-in appears to
 *  succeed) but every write call to /repos/* returns 403 "not
 *  accessible". */
export const GITHUB_OAUTH_SCOPE = 'repo';

/** URL the device-flow client POSTs to for an initial device code.
 *  Composed here so tests and alternate deployments can override the
 *  base. */
export const DEVICE_CODE_URL = `${OAUTH_PROXY_BASE_URL}/login/device/code`;

/** URL the device-flow client polls for the access token, and the
 *  same URL used to redeem refresh tokens for new access tokens. */
export const TOKEN_URL = `${OAUTH_PROXY_BASE_URL}/login/oauth/access_token`;

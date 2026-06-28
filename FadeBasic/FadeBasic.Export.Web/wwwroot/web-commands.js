export function getLocation() {
    return window.location.href;
}

export function getUserAgent() {
    return navigator.userAgent;
}

export function alert(msg) {
    window.alert(msg);
}

// Main-thread no-op: the page already renders the full buffered result at end-of-run.
// Worker mode overrides this via setModuleImports to stream print lines back to the page.
export function onPrint(line) {
    // no-op
}

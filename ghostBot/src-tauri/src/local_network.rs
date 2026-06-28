//! macOS Local Network permission trigger.
//!
//! WebRTC inside WKWebView sends mDNS / local-address UDP from WebKit's
//! separate networking process, which on macOS 15 NEVER triggers the
//! Local Network permission prompt for the host app — the traffic is just
//! silently dropped and the app doesn't even appear in System Settings →
//! Privacy & Security → Local Network. The result is ICE failure whenever
//! the Playground peer is on the same machine or LAN.
//!
//! Workaround: emit one harmless mDNS query from the app process itself at
//! startup. That makes macOS attribute local-network intent to GhostBot,
//! show the prompt (with the NSLocalNetworkUsageDescription string from
//! Info.plist), and list the app in System Settings. Once granted, the
//! permission covers the whole app — including WKWebView's WebRTC.

use std::net::UdpSocket;

/// A minimal DNS query for `_services._dns-sd._udp.local` (PTR), the
/// standard "what services exist" mDNS browse — recognisable, harmless,
/// and ignored by responders unless they have something to say.
const MDNS_SERVICES_QUERY: &[u8] = &[
    0x00, 0x00, // transaction id (0 for mDNS)
    0x00, 0x00, // flags: standard query
    0x00, 0x01, // 1 question
    0x00, 0x00, 0x00, 0x00, 0x00, 0x00, // no answer/authority/additional
    // _services._dns-sd._udp.local
    0x09, b'_', b's', b'e', b'r', b'v', b'i', b'c', b'e', b's',
    0x07, b'_', b'd', b'n', b's', b'-', b's', b'd',
    0x04, b'_', b'u', b'd', b'p',
    0x05, b'l', b'o', b'c', b'a', b'l',
    0x00, // name terminator
    0x00, 0x0c, // type PTR
    0x00, 0x01, // class IN
];

/// Fire-and-forget. Errors are logged and swallowed — this is a permission
/// trigger, not a functional dependency.
pub fn trigger_local_network_prompt() {
    std::thread::spawn(|| {
        match UdpSocket::bind("0.0.0.0:0") {
            Ok(sock) => match sock.send_to(MDNS_SERVICES_QUERY, "224.0.0.251:5353") {
                Ok(_) => eprintln!("[local-network] mDNS probe sent (permission trigger)"),
                Err(e) => eprintln!("[local-network] mDNS probe send failed: {e}"),
            },
            Err(e) => eprintln!("[local-network] socket bind failed: {e}"),
        }
    });
}

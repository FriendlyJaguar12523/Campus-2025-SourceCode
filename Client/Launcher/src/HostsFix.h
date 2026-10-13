#pragma once
// Windows hosts-file fallback.
//
// Some players' ISPs block or null-route dynamic DNS (gameauth.ddns.net), and the game
// stops dead at BootSequence with "error code: summer" - RecNet's NameServer logging a
// NameResolutionFailure. Those players can't reach the server at all.
//
// The fix is a one-time hosts entry pinning camprec.net (and gameauth.ddns.net, cdn.camprec.net) to
// the VPS IP, bypassing the ISP's DNS entirely. The launcher does it for them: on startup it
// checks whether camprec.net resolves; if not, offers a "Fix connection" button that
// relaunches itself elevated (UAC prompt) and writes the entries. The game, launched by this
// same launcher as a child process, sees the same hosts file - no further action needed.
//
// The entries are wrapped in a marked block ("# Campus Launcher ... # end Campus Launcher")
// so WriteHostsBlock replaces its own previous write instead of appending every call, and
// players can remove them cleanly if they ever need to.
#include <string>

namespace hostsfix {

    // The Campus VPS public IP. Mirrors ConfigCampus.CdnBaseUrl / CdnFallbackUrl's host - if
    // this ever changes, bump the launcher version so a fresh exe carries the new IP.
    constexpr const char* kServerIp = "50.114.179.17";

    // The hostnames /ns advertises; both must resolve for the game to connect and bundle-cache.
    // The game resolves EVERY host /ns names before it connects: ns. (the name server), api. (every service) and cdn. (bundles), plus camprec.net
    // (the website / older builds) and gameauth.ddns.net (the old host). If any one doesn't resolve it stops with "error code: summer".
    // The first kProbeCount are the ones the launcher checks at startup and pins; the last is only pinned.
    constexpr const char* kHosts[] = { "camprec.net", "ns.camprec.net", "api.camprec.net", "cdn.camprec.net", "gameauth.ddns.net" };
    constexpr int kHostsCount = (int)(sizeof(kHosts) / sizeof(kHosts[0]));
    constexpr int kProbeCount = 4;

    // true if the hostname resolves right now. Uses getaddrinfo; the OS resolver returns
    // quickly (sub-second) on both success and NXDOMAIN, so this is safe on the UI thread
    // but we still run it on a background thread in case of a hung ISP resolver.
    bool CanResolve(const char* host);

    // Write / replace the Campus block in C:\Windows\System32\drivers\etc\hosts, pointing
    // every host in kHosts at ip. Must be run from an elevated process - the file is
    // non-admin-read-only. Returns true on success.
    bool WriteHostsBlock(const char* ip);

    // ipconfig /flushdns, run silently.
    void FlushDns();

    // Relaunch self with "runas" (UAC) + "--fix-hosts <ip>", wait for exit. Returns true if
    // the elevated instance reported success.
    bool RelaunchElevatedAndFix(const char* ip);

    // If "--fix-hosts <ip>" is on the command line, perform the write + flush and return the
    // process exit code (0 success / non-zero failure). Returns -1 if the flag isn't there,
    // meaning "no action, continue normal launch".
    int MaybeHandleFixHostsFlag();

}

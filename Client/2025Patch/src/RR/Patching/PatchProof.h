#pragma once

// The login patch gate, PC side. The server only lets a PC game login through
// (/Auth/connect/token) after a fresh proof from the CURRENT patch: this DLL signs the SHA-256 of
// its own file (plus the time and this PC's Steam id) with a key shared with the server and POSTs
// it to /api/patch/proof. The server accepts it only if that SHA-256 is the patch the launcher
// serves now, so an older (or anyone else's) DLL can't sign in. See the server's PatchProofs.cs.
namespace PatchProof {
    // Prove now (blocking, a few seconds at most). true = the server accepted it.
    bool Send();
    // Called by the SendRequest hook just before the game's login request goes out (rate-limited).
    void BeforeLogin();
}

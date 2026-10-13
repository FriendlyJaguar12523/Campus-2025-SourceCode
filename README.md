# Campus

A community revival of Rec Room: the servers, the launcher and the PC patch.

| Folder | What it is |
| --- | --- |
| `Servers/Campus` | The game API, the website and the admin panel (ASP.NET, `dotnet build`). Needs a `Data` folder next to it - game data, accounts, uploads - which is not published. |
| `Servers/CampusNet` | The Photon-protocol game server (UDP 5055 / 5056 / 5058). |
| `Servers/Tachyon` | The voice server. |
| `Client/Launcher` | The Windows launcher (C++ / ImGui, Visual Studio). |
| `Client/2025Patch` | The PC patch injected into the game. `Client/2025Patch.sln` builds it; the launcher's release scripts expect the two folders side by side. |
| `Client/Quest2025Patch` | The Quest patcher. |

## What is not here

- Build output, binaries, archives, databases and logs.
- Anything private: accounts, tokens, uploads, the generated launcher / patch proof keys, passwords.
- **Secrets in the code have been replaced with `REDACTED`** (webhooks, bot tokens, API keys, client secrets, admin and signing keys). Search for `REDACTED` and fill in your own before running.
  `LauncherKey.h` and `PatchProofKey.h` are placeholders (`CHANGE_ME`); the release scripts generate real ones.
- The token signing key must be your own: set `CAMPUS_KEY` (or `Data/jwt_key.txt`) and give CampusNet the same value (`CAMPUSNET_JWT_KEY`).

This repository is a published snapshot, rebuilt from the working copy by a script - changes made here are overwritten by the next upload.

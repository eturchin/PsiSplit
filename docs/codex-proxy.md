# Codex with its own background tunnel

`CodexProxy.exe` launches the installed Windows x64 Codex app with a private
Psiphon connection. The PsiSplit window and the separate Psiphon GUI are not
needed. A background launcher and tunnel core remain running while Codex is open.

## Use

1. Finish your work and fully exit any existing Codex instance when convenient.
2. Run `CodexProxy.exe`, or a shortcut pointing to it.
3. Wait for the connection. Codex opens only after the tunnel is ready; initial
   connection can take up to 150 seconds.
4. Fully exit Codex to let the launcher stop its own tunnel. Closing a window
   while Codex remains in the tray keeps the connection alive; use the app's
   **Quit** command in that case. Terminating `CodexProxy.exe` also terminates
   its own Codex tree and tunnel core, so no background process is left behind.

An already open Codex instance is never stopped or reconfigured. The launcher
asks you to exit it yourself later. Running the shortcut again while a proxied
launch is active brings the existing Codex window to the foreground instead of
opening a second tunnel. Keep using the dedicated shortcut for future proxied
starts; ordinary Store/taskbar shortcuts keep their original behavior.

## External local proxy

To use an existing local HTTP CONNECT proxy, run:

```powershell
./CodexProxy.exe --proxy-port 14080
```

The launcher checks the public ChatGPT page and unauthenticated Codex service
response before opening Codex. Keep the external proxy running during the
session. Its speed and availability depend on the chosen provider and route.

Check a proxy and write a report without opening Codex:

```powershell
./CodexProxy.exe --check-proxy 14080 ./proxy-check.json
```

Exit code 0 means the public reachability checks passed. Exit code 3 means they
failed. This check does not sign in or measure authenticated model requests.

## What it changes

- It sets the Electron HTTP proxy argument and proxy environment variables for
  the new Codex process and its compatible child processes only.
- The app's `CODEX_CLI_PATH` override points at a small byte-preserving bridge.
  The bridge starts the installed `codex.exe` with
  `-c features.respect_system_proxy=false`, so a saved system-proxy preference
  cannot take precedence over this process's proxy variables. It forwards the
  original arguments, stdin, stdout and stderr and returns the native exit code.
- It resolves the versioned `WindowsApps` installation directory on every launch.
  No user-specific or fixed package-version paths are stored in source.
- The connection uses Automatic country selection and free loopback ports. A
  stable local endpoint survives tunnel restarts and reconnects automatically.
- State and private logs are stored under `%LOCALAPPDATA%\CodexProxy`, separately
  from PsiSplit's active connection. It changes neither Windows proxy/PAC
  settings nor the user's Codex configuration, authentication or chat history.
- Normal shutdown stops only the launcher-owned tunnel. The launcher and the
  runtime bridge own Windows jobs, so terminating either one also stops the
  processes it started; a hidden error dialog cannot keep the launcher alive.

This is application-supported HTTP/HTTPS/WebSocket proxying, not a transparent
per-process VPN or a packet-level traffic block. Tools launched by Codex can
inherit the proxy variables; tools that ignore proxy settings need their own
configuration. The installed app's `CODEX_CLI_PATH` behavior is verified locally
and may need adjustment after future Codex changes.

## Build and inspect

Build from the repository root with the .NET 8 SDK:

```powershell
./publish-codex-launcher.ps1
```

Output: `dist/codex-proxy/CodexProxy.exe`, self-contained with the bundled engine.
Optionally run `./tools/install-codex-launcher.ps1` to copy the launcher to
`%LOCALAPPDATA%\Programs\CodexProxy` and add **Codex (Proxy)** shortcuts on the
Desktop and in the Start menu. Installation never launches or closes Codex.
To remove it, delete those two shortcuts and the installed `CodexProxy` folder
after fully exiting its Codex session. Private state is under
`%LOCALAPPDATA%\CodexProxy` and can be removed separately.

To inspect the installed package without starting Codex or a tunnel:

```powershell
./dist/codex-proxy/CodexProxy.exe --check ./artifacts/codex-launcher-check.json
```

The report contains the Codex and launcher versions plus boolean readiness
fields, no credentials.
Generated binaries, reports and shortcuts belong in ignored `dist`/`artifacts`
directories. Keep the third-party notices and Psiphon license with redistributions;
see the repository's `THIRD-PARTY-NOTICES.md` for upstream source links.

Proxy behavior references: [Electron switches](https://www.electronjs.org/docs/latest/api/command-line-switches#--proxy-serveraddressport)
and [Codex native proxy behavior](https://learn.chatgpt.com/docs/enterprise/managed-configuration#use-the-system-proxy-for-native-client-requests).

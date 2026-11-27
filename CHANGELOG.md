# Changelog

## 1.3.0 - 2026-11-27

- Add a `Reconnecting` state so brief relay outages no longer look like a terminal failure and no longer disable Site mode.
- Replace the remote CONNECT health probe with a local listener and process check every five seconds. Two transient remote failures can no longer stop an otherwise healthy tunnel.
- Keep waiting up to 75 seconds for the core to rebuild a lost relay, and up to 15 seconds for an unresponsive local listener. An exited core fails immediately.
- Add automatic reconnect after a terminal failure with a 3/8/15/30/60-second backoff. It is enabled by default in Settings; an explicit Stop cancels it.
- Keep Site mode applied while automatic reconnection runs, so listed sites resume without user action.
- Remove the fixed 15-minute relay timeout that interrupted long downloads, uploads, and WebSockets.
- Use pooled 64 KB relay buffers, enable TCP keepalive on accepted and direct upstream sockets, and stop logging every successful request at Info level.
- Replay existing notices when reattaching to a detached core so tunnel state, exit country, and traffic totals are restored.
- Document that the selected Psiphon exit server, not PsiSplit's local relay, is normally the speed bottleneck.

### Codex launcher

- Add an optional standalone Codex launcher that connects its own background tunnel before opening the app.
- Route the Electron UI and native Codex runtime through a process-local proxy without changing Windows proxy settings or the saved Codex profile.
- Add a separate publisher and an installer for Desktop/Start menu shortcuts; refuse to restart existing Codex sessions.
- Keep the Codex launcher, its private tunnel and every Codex child process in one Windows job. Terminating `CodexProxy.exe` now also removes the tunnel core and Codex instead of leaving orphaned processes.
- Never show a hidden modal error when cleanup races a process exit; shutdown failures are written to the private log and the launcher still exits.
- Stop the runtime bridge when its owning launcher disappears, and bound stream draining so a grandchild holding a pipe cannot keep `CodexProxy.exe` alive.
- Treat detach/session-file and monitor failures as warnings so shutdown cannot fail because a state file is temporarily locked.
- Make stopping the tunnel core tolerate a process that exits between the status check and the kill request.

- Bring an existing proxied Codex window to the foreground when its shortcut is started again.
- Support an external local HTTP CONNECT proxy with --proxy-port and a read-only --check-proxy reachability report.

Validation: 116 local and isolated checks passed, including real tunneled traffic, core crash detection, country changes and preservation of existing processes and Windows proxy settings.

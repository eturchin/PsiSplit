# Architecture

[Back to README](../README.md)

PsiSplit is a Windows WPF shell around a bundled Psiphon Console Client and two local routing proxies. The application does not install a packet capture driver or a virtual network adapter.

## Components

| Module | Responsibility |
| --- | --- |
| `PsiSplit.App` | WPF views, tray actions, app/site editing, connection controls, and counters. |
| `PsiphonBundle` | Reads embedded resources, verifies SHA-256 hashes, extracts the core, and creates an isolated core configuration. |
| `PsiphonController` | Starts the core without a window, reads notices, checks readiness, monitors health, and owns its lifecycle. |
| `SmartProxyServer` | Accepts HTTP requests and CONNECT tunnels; selects the core or a direct TCP connection. |
| `DomainMatcher` | Compiles enabled hostname rules for the filtered proxy. |
| `PsiphonRegionCatalog` | Reads country codes from the bundled seed list for offline selection and resolves country names. |
| `TransferRateSampler` | Calculates byte rates from cumulative counters using actual monotonic elapsed time. |
| `PacGenerator` / `PacServer` | Generate and serve the browser routing script. |
| `SystemProxyManager` | Saves, applies, watches, and restores the Windows PAC/proxy settings. |
| `AppLauncher` / `ShellLauncher` | Launch proxy-aware programs with the selected local endpoint. |
| `ConfigStore` / `LogService` | Persist user settings and local daily logs. |
| `PsiSplit.SelfTest` | Local checks and isolated or explicitly opted-in system integration tests. |

## Startup and connection

1. Normal startup takes a per-user/session singleton mutex so two regular instances cannot compete for the same local ports and PAC setting.
2. The app loads its own profile. If a previous PsiSplit run left a proxy backup, startup checks whether the current PAC looks like a local PsiSplit PAC before attempting recovery.
3. The Full, Filtered, and PAC services bind to loopback. Starting these services does not itself start a tunnel.
4. **Start VPN** acquires the tunnel controller's private directory lease. A second controller cannot concurrently manage the same datastore.
5. Embedded resources are verified and extracted into a directory named after the core's SHA-256. A new engine version receives a different directory instead of replacing a running binary.
6. PsiSplit writes a core configuration pointing only at its private data directory, with the selected `EgressRegion` (empty for Automatic). Both core proxy ports are set to `0`, asking Windows to allocate free ports. The default listen interface is loopback. Non-diagnostic byte-transfer notices and country reporting are enabled.
7. PsiSplit starts the core directly with `--config`, `--serverList`, and `--notices`. It never launches the official GUI. GUI upgrade downloads and feedback upload are disabled in the generated configuration.
8. JSON notices announce the HTTP/SOCKS ports and tunnel count. A positive tunnel count and a successful CONNECT request are both required before PsiSplit reports the connection as ready.
9. The routing proxies receive the assigned upstream HTTP port. If configured, Site mode applies the PAC only after connection succeeds.

The bundled seed list bootstraps discovery. Signed remote lists and the core's own protocol selection handle subsequent server choice; PsiSplit does not maintain a static list of live exit addresses or force one remote transport port. The initial preference is Netherlands for compatibility with earlier builds; the Servers tab can select another country or Automatic.

## HTTP, HTTPS, and routing

The Full proxy sends each accepted request through the core's local HTTP proxy. The Filtered proxy checks the requested hostname against enabled rules and selects either that upstream or a direct connection.

For HTTPS, PsiSplit forwards CONNECT and then relays the bidirectional byte stream. It does not terminate TLS or install a trust certificate. For ordinary HTTP, it rewrites the request target for the chosen upstream and relays the response. The proxy is intentionally small and generally uses one request per client connection rather than implementing a full general-purpose HTTP cache or connection pool.

If a route requires Psiphon and the core is not ready, the local routing proxy returns an HTTP error. Unlisted direct routes in the Filtered proxy do not require the tunnel.

Counters track relayed bytes and selected routes at the PsiSplit proxy layer. They are not packet-level system accounting and do not include every byte of the core's encrypted network transport.

The prominent VPN speed counters use the core's `BytesTransferred` notices, which contain 64-bit sent/received payload deltas. The controller accumulates them; a UI timer samples the totals once per second using a monotonic stopwatch. Additional UI refreshes do not consume samples. Delayed ticks use their actual elapsed duration, and idle or reset counters do not retain an old speed. Each local proxy also has its own rate sampler, so direct Filtered traffic remains distinguishable from tunneled payload.

## Country selection

`AvailableEgressRegions` supplies the live country candidates. The latest report is cached locally; before any report, country codes are decoded from the bundled seed records without exposing server credentials or requiring the live datastore. Cache/bundled entries are labeled as such. `ConnectedServerRegion` supplies the actual connected country independently of the requested preference.

Saving a country updates the profile without modifying the active controller. An explicit start/reconnect creates a controller for the saved choice. Detached sessions include a configuration identity, so changing the requested country cannot accidentally adopt a tunnel established for another country or an older core configuration lacking transfer notices.

## PAC Site mode

The PAC is served from loopback and installed through the current user's Windows Internet Settings. Its behavior is:

```javascript
// Illustrative rule; the actual file is generated from the enabled site list.
function FindProxyForURL(url, host) {
  if (host === "example.com" || dnsDomainIs(host, ".example.com")) {
    return "PROXY 127.0.0.1:18081; DIRECT";
  }
  return "DIRECT";
}
```

The `DIRECT` fallback is intentional. A compatible browser may bypass an unavailable proxy, so Site mode is not a kill switch. PAC also applies to compatible programs that were not launched by PsiSplit. It does not govern clients with independent network settings.

PsiSplit saves the previous proxy snapshot before applying Site mode and restores it when the mode is disabled or the app exits normally. The watchdog checks for unexpected changes while Site mode is active. Changing a rule updates the PAC and a version parameter in its URL; the exact reload behavior still depends on the client.

## Lifecycle, retries, and ownership

The controller moves through `Stopped`, `Starting`, `Connected`, `Stopping`, and `Failed`. Startup has a bounded connection timeout. The health loop checks the tunnel periodically and reports failure after repeated unsuccessful probes. The UI disables Site mode when a failure is detected.

Stop targets the retained process handle for the owned core. The controller does not discover ownership from process names, collect every newly appearing core PID, or kill another running Psiphon instance. A startup error or cancellation cleans up only the process created or verified by that controller.

If disconnect-on-exit is disabled, disposal detaches from the owned core. A saved session records its PID, creation time, exact private executable path, proxy ports, configuration identity, and notice filename. A later controller verifies those identity boundaries before adoption. A recycled PID, a process at another path, or copied session metadata is not enough to claim ownership. A previously bundled version can be retired before a new version opens the private datastore.

Each launch gets a unique notice file in the private `notices/` directory. Reconnect does not truncate a file retained by a previous reader or antivirus. Stop attempts to remove that owned stream after process exit; an unavailable old file does not prevent the next launch. Detach leaves its stream available for reattachment.

## Files and isolation

User configuration lives under `%APPDATA%/PsiSplit`; engine state lives under `%LOCALAPPDATA%/PsiSplit/Tunnel`. Neither uses the official Psiphon client's datastore or registry configuration. Compatibility fields from older PsiSplit profiles can still be read, but the old GUI path and fixed upstream port are ignored by the new engine launcher.

`--preview` creates a fresh demonstration profile and opens only the interface. It bypasses the normal singleton without starting routing services, the tunnel, tray integration, proxy recovery, or the watchdog. Actions are disabled/intercepted; tab navigation and scrolling remain available for screenshots. It can run alongside an existing session.

For testing, each real-engine run uses a unique directory and OS-assigned core ports. The isolated suite compares registry snapshots and pre-existing process identities before and after its run.

## Boundaries

Environment variables and browser arguments are configuration, not interception. A program that ignores them can connect directly. UDP is not routed by these HTTP proxies. The app does not enforce firewall rules, system-wide DNS routing, or prevention of direct traffic. Local proxy endpoints are unauthenticated and intended only for the local machine.

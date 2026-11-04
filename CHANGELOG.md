# Changelog

## 1.2.0 - 2026-11-04

- Show current VPN download/upload speed from Psiphon's payload-transfer notices.
- Show separate Full and Filtered proxy rates, alongside transferred bytes and connection counters.
- Calculate rates with actual elapsed sample time; handle idle periods, delayed UI updates, and restarted counters.
- Add a Servers tab with exit-country selection and Automatic mode. Psiphon chooses a server within the selected country.
- Update country candidates from the core, with clearly labeled cached or bundled lists before a live report arrives.
- Show the connected exit country independently of the saved preference.
- Save preferences without disconnecting; apply changes through an explicit connect/reconnect action.
- Validate detached-session configuration before reuse so a changed country cannot reuse an old-country tunnel.
- Use separate notice files per engine launch so readers holding an old file cannot block reconnect.
- Preserve the Netherlands preference for profiles created by earlier versions.
- Extend local and isolated tests for transfer rates, country selection, cache parsing, session changes, and notice-file contention.

Validation: 79 local checks and 103 isolated checks passed, including real connections through the Netherlands and Germany and preservation of pre-existing processes and proxy settings.

## 1.1.0 - 2026-11-02

- Bundle the unmodified Psiphon tunnel core and public bootstrap inside the standalone Windows executable.
- Launch the core directly without opening or requiring the separate Psiphon GUI.
- Provide Full/Filtered proxies, browser PAC routing, app profiles, shell shortcuts, traffic counters, and tray operation.
- Add isolated engine lifecycle tests, an offline documentation preview, and English documentation.

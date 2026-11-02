# Privacy and repository contents

[Back to README](../README.md)

## What is published

The repository contains application source, portable build scripts, documentation, icon assets, generic examples, offline preview screenshots, and a specifically identified upstream tunnel-core bundle.

The bundle's configuration is public network bootstrap material, not a copy of the complete installed client configuration. Public signature verification keys and channel/sponsor identifiers are required for the core to discover and verify its network resources. The seed server list is supplied by the upstream distribution. Its encoded connection fields are part of that distribution's bootstrap, not personal account credentials.

The importer uses a field allowlist. It does not import authorizations, upstream proxy credentials, machine/network identifiers, user directories, opaque overrides, or the official client's live server datastore. Exact provenance and hashes are in [the third-party notices](../THIRD-PARTY-NOTICES.md).

## What stays local

`.gitignore` excludes:

- Build/publish outputs, debug symbols, dumps, and local verification reports.
- IDE settings and workspace-specific tool state.
- Runtime configuration, app profiles, logs, saved proxy settings, and session records.
- Psiphon's generated datastore, notices, and lock files.
- Environment files, private key/certificate files, and arbitrary executables/libraries.

Only the explicitly named, unmodified upstream core executable is allowed through the binary ignore rule. Ready-to-run application builds are distributed as release assets rather than committed into source history.

README commands are relative to the checkout. Example app paths use environment variables. Build source paths are mapped to a neutral prefix, release publishing disables debug symbols, and documentation images are captured from a fresh demonstration profile.

## Local information that can be sensitive

Your actual app profile may contain executable paths and arguments. Logs can include requested hostnames, ports, application names, exception messages, or local paths. The proxy backup records previous Windows proxy settings; session files record process identity. The Psiphon engine keeps its own cache/notices and also communicates with the Psiphon network to establish a tunnel.

Do not attach a complete data directory to a public issue. Share a minimal, reviewed excerpt instead. The application has no repository upload step; version-control publication is a separate developer action.

## Check before publishing

`tools/check-privacy.ps1` inspects tracked/staged files for local checkout/home/computer identifiers, the locally configured personal Git email, absolute user paths, credential/private-key signatures, unexpected binaries, and generated-state paths. It checks binary resources for exact local identity strings too. It prints the affected file and category rather than the matched value.

This check is a practical guard, not a guarantee against every secret format. Review staged changes and screenshots. `.gitignore` prevents future accidental additions; it does not erase files already committed. This repository's initial commit is created only after the staged audit, so local build reports and profiles are absent from its initial history.

# Psiphon tunnel core

PsiSplit bundles an unmodified Psiphon Console Client from the user's existing
official Psiphon Windows distribution. Psiphon is copyright Psiphon Inc. and
contributors and is licensed under GNU GPL version 3 or later. The complete
license is included as `Psiphon-LICENSE.txt` in the standalone distribution and
embedded in the executable. It is also extracted alongside the tunnel core.

- Source repository: https://github.com/Psiphon-Labs/psiphon-tunnel-core
- Corresponding source revision: https://github.com/Psiphon-Labs/psiphon-tunnel-core/tree/24b8381cc3
- Build date reported by the binary: 2026-07-23T19:15:13+00:00
- Build toolchain reported by the binary: go1.26.5
- Core SHA-256: `2EDA700D3D5723F6C792718A5B7CFE5D35A9888168B539EF042312805A1D2E02`
- Upstream dependencies and their licenses: see `go.mod`, `vendor/modules.txt`
  and dependency license files at the corresponding source revision.

`bootstrap.json` contains only public Psiphon network bootstrap settings:
channel/sponsor identifiers, signature verification public keys and signed
server-list download locations, plus the selected exit region. `server_list.dat` is the seed list supplied
with the installed client. Account authorizations, network identifiers,
upstream proxy credentials, opaque overrides, user directories and the running
client's datastore are not imported.

Use `tools/import-psiphon-core.ps1` for a read-only refresh from an official
installation; update this notice and the license to match a new upstream
revision. The manifest records the imported engine version and resource hashes.
No Psiphon GUI binary is included or launched by the new PsiSplit build.

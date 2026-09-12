# Offline package evidence, September 12, 2026

The normal build and the `-OfflineBuild` cache-only build both completed from committed source
`54fd076b5394b896f9332d59a409b1bf5fe2cc22`, with README, TODO, DESIGN and packaging instructions
already included in that source commit. The later evidence commit does not change the executable.

The local acceptance artifact is the cache-only build:

```text
D:\Projects\GoldenTicket\artifacts\release\GoldenTicket-win-x64-20260912-182408-54fd076b-1db8087a\GoldenTicket-win-x64-20260912-182408-54fd076b-1db8087a.zip
```

Its already-extracted `GoldenTicket` subdirectory contains `GoldenTicket.exe`. Keep the complete
directory together. The ZIP is a local artifact, not a committed binary or published GitHub release.

| Property | Verified value |
|---|---|
| Source commit | `54fd076b5394b896f9332d59a409b1bf5fe2cc22` |
| SDK | .NET 10.0.401 |
| Included runtimes | .NET, WPF and ASP.NET 10.0.12, Windows x64 |
| Lock selection | Seven reviewed `packages.win-x64.lock.json` files; original development locks unchanged. |
| Offline restore | Existing package cache and empty local feed, with remote NuGet feeds/audit disabled. |
| Executable diagnostic | Eight checks passed, with CoreCLR actually loaded from the package directory. |
| ZIP contents | 669 payload files plus checksum manifest; each decompressed entry's length and SHA256 independently matched the manifest. |
| ZIP SHA256 | `df28142da2b1e1b76ccc8e5ccf861d8974fec9dacd08c1d98543f61f184b2ecd` |
| Signing / installer | Neither provided. This is a portable ZIP. |

Retained records:

- [Artifact size, digest and completion time](package-result.json)
- [Executable component results](runtime-diagnostic.json)
- [Runtime/source provenance and remaining gates](package-provenance.json)
- [Exact dependency metadata and notice inventory](dependencies.json)

The first build attempt at `88f4435` stopped before publication because the normal referenced-project
locks did not include the requested runtime identifier. Dedicated runtime locks corrected that
failure. The normal-mode ZIP built from the final source had SHA256
`41585ccf78efebe2b8b3be1965cb23ced3072a4c931b1ebe8e35f7d9ac02c23e`; it has the same source and
runtime versions, but its provenance records a connected restore. Each run has a different output
directory and provenance timestamp, so archive digests differ deliberately.

The diagnostic opened no visible window, camera, network listener or player save. It rendered WPF,
queried SQLite in memory, protected/unprotected synthetic bytes with current-user DPAPI, encoded
and decoded a synthetic PNG through Windows/WPF, constructed ASP.NET without listening, checked
the board checksum and local PWA assets, and verified the loaded runtime directory.

This proves the local build/cache path and bundled-component loading on the developer machine.
It does not establish a clean Windows machine, disabled-WAN complete match, actual camera or phone
installation, UI focus/scaling, or physical photo/rebuild acceptance. Those checks remain in
[the morning walkthrough](../../IMPLEMENTATION-2026-09-12.md).

Several upstream packages supply license expressions/URLs without full license text. Their exact
metadata is retained in the ZIP, and the provenance lists notice-text review still required before
wider distribution. No release tag or release publication was created.

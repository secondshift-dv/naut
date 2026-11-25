# Development Build and Release Reference

This document is contributor-facing. User documentation starts at [../README.md](../README.md).

## Build

The public source target is Windows x64 with .NET SDK 10.0.401.

```powershell
pwsh ./scripts/verify.ps1 -Scope Source
pwsh ./scripts/build.ps1
```

The canonical build writes the portable application and verified release inputs under `dist/`. The launcher and replacement helper are compressed self-contained executables. App and Runtime track `ProductVersion`. The top-level launcher and other supporting components report their component versions in Windows file properties. To identify the packaged product, check `release-manifest.json` and the exact source commit in `build-provenance.json`.

## GitHub Actions

**Source Verify** runs on pushes and pull requests to `main`. It checks public hygiene, documentation, workflow policy and all shipped projects. **Build (Manual)** checks the canonical package, updater and release layout. Its Actions artifact is opt-in. **Optional Packs** validates pack sources and their presentation engine dependencies using read-only CI. Manual dispatch can prepare archives or, with publish=true on public main, invoke the dedicated signing and Pages publication job. Publication checks the exact archive inventory, digests and signature, preserves older downloads, and removes the consumed handoff. Confirm the Pages deployment separately when a rebuild is required.

## Release authority

The public repository `secondshift-dv/naut` is the only official GitHub Release authority for Naut application releases. The private engineering repository `naut-dv` does not publish official application releases.

Release publication verifies the exact source HEAD, canonical build provenance, payload checksum, corresponding-source companions, and the pinned update publisher key before creating a signed release. Before publication, the exact v0.0.1, v0.0.2, and v0.0.18 updater sources must accept the final signed manifest, verify the full ZIP, and validate its complete staging directly.

Naut v0.0.3 and later clients can reuse installed files only after byte-length and SHA-256 verification. Changed and new files are transferred through content-addressed `update-file-<sha256>.bin` release assets. The signed manifest binds the protocol metadata in `update-incremental.json`. Full ZIP packages remain available for older clients, manual installation and recoverable transport fallback; signature, identity and integrity failures stop the update. Current clients download each required file identity once per operation, even if several target paths require the same bytes. Download progress counts the bytes transferred for those identities. Zero-length members are specified completely by their signed length and empty SHA-256, so staging creates them locally without a remote asset or transfer. Both paths build a complete isolated target before the existing backup, replacement and recovery flow. Application replacement never mutates the Vault. Catalog schema upgrades occur separately when the application opens a compatible Vault; back up the complete Vault before migrating to schema 4.

Canonical releases include the full ZIP, `update.json`, `update-signature.json`, `update-incremental.json`, all content-addressed file assets, `build-provenance.json`, and both corresponding-source archives.

The public **Release (Manual)** workflow runs from `main`, one release run at a time. A read-only job builds the selected commit and hands off only publication files. A dedicated job signs and publishes the payload and removes the handoff after success. A failed publication retains the handoff for a retry until its one-day expiration.

## Dependency source releases

FFmpeg/OpenCV support releases exist to satisfy reproducibility and corresponding-source obligations. They are support artifacts, not Naut application versions, and must remain marked as prereleases so the application's `releases/latest` feed resolves to the current Naut version.

## Update signing

The private signing key is never stored in either repository. CI receives it only through the `NEUTERRADISE_UPDATE_SIGNING_KEY_PEM` repository secret. The matching public key and key id are pinned in `release-contract.json`.

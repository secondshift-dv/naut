# Development Build and Release Reference

This document is contributor-facing. User documentation starts at [../README.md](../README.md).

## Build

The public source target is Windows x64 with .NET SDK 10.0.401.

```powershell
pwsh ./scripts/verify.ps1 -Scope Source
pwsh ./scripts/build.ps1
```

The canonical build writes the portable application and verified release inputs under `dist/`.

## Release authority

The public repository `secondshift-dv/naut` is the only official GitHub Release authority for Naut application releases. The private engineering repository `naut-dv` does not publish official application releases.

Release publication verifies the exact source HEAD, canonical build provenance, payload checksum, corresponding-source companions, and the pinned update publisher key before creating a signed release. Before publication, the exact v0.0.1 and v0.0.2 updater sources must accept the final signed manifest, verify the full ZIP, and validate its complete staging directly.

Naut v0.0.3 and later clients can reuse installed files only after byte-length and SHA-256 verification. Changed and new files are transferred through content-addressed `update-file-<sha256>.bin` release assets. The signed manifest binds the protocol metadata in `update-incremental.json`. Full ZIP packages remain available for older clients, manual installation and recoverable transport fallback; signature, identity and integrity failures stop the update. Byte progress uses the actual planned transfer rather than the full release size. Zero-length members are specified completely by their signed length and empty SHA-256, so staging creates them locally without a remote asset or transfer. Both paths build a complete isolated target before the existing backup, replacement and recovery flow. Updates never mutate the Vault.

Canonical releases include the full ZIP, `update.json`, `update-signature.json`, `update-incremental.json`, all content-addressed file assets, `build-provenance.json`, and both corresponding-source archives.

## Dependency source releases

FFmpeg/OpenCV support releases exist to satisfy reproducibility and corresponding-source obligations. They are support artifacts, not Naut application versions, and must remain marked as prereleases so the application's `releases/latest` feed resolves to the current Naut version.

## Update signing

The private signing key is never stored in either repository. CI receives it only through the `NEUTERRADISE_UPDATE_SIGNING_KEY_PEM` repository secret. The matching public key and key id are pinned in `release-contract.json`.

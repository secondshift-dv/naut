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

Release publication verifies the exact source HEAD, canonical build provenance, payload checksum, corresponding-source companions, and the pinned update publisher key before creating a signed release.

## Dependency source releases

FFmpeg/OpenCV support releases exist to satisfy reproducibility and corresponding-source obligations. They are support artifacts, not Naut application versions, and must remain marked as prereleases so the application's `releases/latest` feed resolves to the current Naut version.

## Update signing

The private signing key is never stored in either repository. CI receives it only through the `NEUTERRADISE_UPDATE_SIGNING_KEY_PEM` repository secret. The matching public key and key id are pinned in `release-contract.json`.

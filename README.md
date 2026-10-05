![Naut wordmark](src/Neuterradise.App/Assets/Brand/wordmark-lockup.png)

[English](README.md) · [Deutsch](README.de.md) · [Español](README.es.md) · [Français](README.fr.md) · [Bahasa Indonesia](README.id.md) · [日本語](README.ja.md) · [한국어](README.ko.md) · [简体中文](README.zh-Hans.md)

# Naut

**A Navigator for Your Things Worth Keeping**

Naut is a local-first media collection manager for Windows. Profiles bring images, videos, and interactive 3D Figures together in a portable Vault while keeping the collection under the user's control.

The private **naut-dv** repository is the engineering authority. This curated **naut** repository is the public source, contribution, and official release surface. Naut-owned source is **Source Available** under PolyForm Shield 1.0.0; third-party components keep their own licenses.

## Build

Windows x64 and .NET SDK **10.0.401** are required.

```powershell
pwsh ./scripts/verify.ps1 -Scope Source
pwsh ./scripts/build.ps1
```

The portable package starts with `naut.exe`; keep `runtime/` and `release-manifest.json` beside it.

## Official downloads

Official Naut binaries and signed update metadata are distributed only through the official **secondshift-dv/naut GitHub Releases** page. Do not run binaries re-uploaded to Discord, file-sharing services, or third-party mirrors.

## Project policies

See [LICENSE](LICENSE), [licensing scope](docs/licensing.md), [brand policy](BRAND-POLICY.md), [contributing](CONTRIBUTING.md), [security](SECURITY.md), and [third-party notices](THIRD-PARTY-NOTICES.txt).

Official application locales: English, German, Spanish, French, Indonesian, Japanese, Korean, and Simplified Chinese.

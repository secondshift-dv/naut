<p align="center">
  <img src="src/Neuterradise.App/Assets/Brand/wordmark-lockup.png" alt="Naut" width="460">
</p>

<p align="center"><strong>A Navigator for Your Things Worth Keeping</strong></p>

<p align="center">Local-first media collections for Windows — offline face intelligence, interactive 3D Figures, and your own portable Vault.</p>

<p align="center">
  <a href="https://github.com/secondshift-dv/naut/releases/tag/v0.0.4"><strong>Download Naut v0.0.4</strong></a>
  ·
  <a href="https://secondshift-dv.github.io/naut/"><strong>View Live Demo</strong></a>
  ·
  <a href="docs/README.md"><strong>Documentation</strong></a>
</p>

<p align="center">
  <sub><strong>README:</strong>
  <a href="README.md">English</a> ·
  <a href="README.de.md">Deutsch</a> ·
  <a href="README.es.md">Español</a> ·
  <a href="README.fr.md">Français</a> ·
  <a href="README.id.md">Bahasa Indonesia</a> ·
  <a href="README.ja.md">日本語</a> ·
  <a href="README.ko.md">한국어</a> ·
  <a href="README.zh-Hans.md">简体中文</a>
  </sub>
</p>

<p align="center">
  <img src="docs/assets/readme/naut-showcase.gif" alt="Naut interactive showcase" width="682">
</p>

## Your collection, made worth exploring.

Naut is a **local-first media collection manager for Windows**. It turns images, videos, and supported 3D models into Profile-centered collections that can be recognized, presented, and explored without moving ownership into a cloud service.

## Feature highlights

<table>
<tr>
<td width="50%" valign="top">
<a href="docs/face-intelligence.md"><img src="docs/assets/readme/feature-face-intelligence.svg" alt="Face intelligence" width="100%"></a><br>
<strong>Face intelligence</strong><br>
YuNet detects applicable faces, SFace produces embeddings, and Naut can surface Profile candidates from confirmed identity samples. Processing stays local and suggestions remain reviewable.
</td>
<td width="50%" valign="top">
<a href="docs/figures.md"><img src="docs/assets/readme/feature-3d-figures.svg" alt="Interactive 3D Figures" width="100%"></a><br>
<strong>Interactive 3D Figures</strong><br>
Compatible models can become interactive Figures with rotate, pan, zoom, Naut-managed framing, and a bounded runtime texture policy.
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/import-media.md"><img src="docs/assets/readme/feature-smart-import.svg" alt="Smart media import" width="100%"></a><br>
<strong>Smart media import</strong><br>
The same import path prepares only what each media type needs: metadata, thumbnails, bounded video presentation media, face analysis when applicable, and Figure derivatives for supported models.
</td>
<td width="50%" valign="top">
<a href="docs/customization.md"><img src="docs/assets/readme/feature-customization.svg" alt="Presentation without destructive edits" width="100%"></a><br>
<strong>Presentation without destructive edits</strong><br>
Customize Home, Gallery, Cards, Profiles, Covers, Banners, Frames, Backdrops, layouts, effects, and themes while leaving the original media intact.
</td>
</tr>
<tr>
<td width="50%" valign="top">
<a href="docs/languages.md"><img src="docs/assets/readme/feature-multilingual.svg" alt="8 interface languages" width="100%"></a><br>
<strong>8 interface languages</strong><br>
Naut ships with English, Bahasa Indonesia, 日本語, 한국어, 简体中文, Deutsch, Français, and Español. Interface language does not rewrite your Vault or media metadata.
</td>
<td width="50%" valign="top">
<a href="docs/vault.md"><img src="docs/assets/readme/feature-local-vault.svg" alt="Portable local Vault" width="100%"></a><br>
<strong>Portable local Vault</strong><br>
The portable app package and the Vault are separate boundaries. Imports copy files into the Vault while the source files remain where they were.
</td>
</tr>
</table>

## Why Naut

- **Profiles, not folders first.** A Profile can represent a person, character, project, object, subject, or any collection identity.
- **Local control by design.** The authoritative collection lives in the Vault you choose.
- **Prepared for browsing.** Naut generates bounded presentation derivatives instead of modifying original media.
- **3D is additive, not mandatory.** Normal Naut use does not require a discrete GPU or a Figure.
- **Portable application, durable collection.** Updating the app package does not replace the Vault.

## See it in action

<table>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-home.png" alt="Home — featured Profiles, activity, and collection context." width="100%"><br><sub>Home — featured Profiles, activity, and collection context.</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-profile.png" alt="Profile — identity, media, presentation, and optional interactive Figure." width="100%"><br><sub>Profile — identity, media, presentation, and optional interactive Figure.</sub></td>
</tr>
<tr>
<td width="50%"><img src="docs/assets/readme/showcase-gallery.png" alt="Gallery — collection browsing, search, filters, and card presentation." width="100%"><br><sub>Gallery — collection browsing, search, filters, and card presentation.</sub></td>
<td width="50%"><img src="docs/assets/readme/showcase-settings.png" alt="Settings — Theme, language, Vault controls, and system-facing options." width="100%"><br><sub>Settings — Theme, language, Vault controls, and system-facing options.</sub></td>
</tr>
</table>

## Quick Start

1. Open the official [Naut v0.0.4 download](https://github.com/secondshift-dv/naut/releases/tag/v0.0.4) and choose **`naut-v0.0.4-win-x64.zip`** under Assets.
2. Extract the complete ZIP to a normal folder.
3. Run `naut.exe`.
4. Choose **where to keep** your Vault.
5. Import media or create a Profile and use **Add media**.

The portable package must keep `runtime/` and `release-manifest.json` beside `naut.exe`. For a browser preview that does not touch local files or a Vault, open the [Interactive Showcase](https://secondshift-dv.github.io/naut/).

## System Requirements

Naut's normal collection experience is intentionally lighter than its optional Figure workload.

| | Minimum | Recommended |
| --- | --- | --- |
| **OS** | Windows 10/11 64-bit | Windows 11 64-bit |
| **CPU** | x86-64, 2 cores | Modern x86-64, 4+ cores |
| **RAM** | 8 GB | 16 GB |
| **GPU** | Integrated graphics for normal Naut use | Direct3D 11-capable integrated/discrete GPU for smoother Figure workloads |
| **Display** | 1280 × 720 | 1920 × 1080 or higher |
| **Free app/update space** | 3 GB | 3 GB+ on SSD |

Vault storage is separate and depends on the size of your collection. See the full [Naut v0.0.4 System Requirements](docs/system-requirements.md).

## Optional pack collection

Make Naut yours with reviewed, independently versioned customization packs.
Download a pack, import it in **Settings → Presentation → Packs / Advanced**,
then choose its components and save. Application code stays unchanged.

**[Browse and download packs](packs/README.md) · [Install or update a pack](docs/pack-collection.md) · [Contribute your own](docs/contributing-packs.md)**

## Documentation

Start with the [Naut Documentation](docs/README.md): **Getting Started** · **Import Media** · **Face Intelligence** · **Profiles** · **Vault** · **Customization** · **Languages** · **3D Figures** · **System Requirements** · **FAQ & Troubleshooting**.

Contributor-facing build, licensing, and Presentation Pack material lives under [docs/development](docs/development/).

## Source and releases

The private **naut-dv** repository is the engineering authority. This curated **naut** repository is the public source, contribution, demo, and official release surface.

Naut-owned source is **Source Available** under PolyForm Shield 1.0.0; third-party components retain their own licenses. See [LICENSE](LICENSE), [licensing scope](docs/development/licensing.md), [brand policy](BRAND-POLICY.md), [contributing](CONTRIBUTING.md), [security](SECURITY.md), and [third-party notices](THIRD-PARTY-NOTICES.txt).

Official binaries and signed update metadata are distributed only through the official **GitHub Releases** page for `secondshift-dv/naut`.

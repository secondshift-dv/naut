# Optional pack collection

[Browse and download official packs](../packs/README.md).
Packs can customize themes, fonts, controls, Home, Gallery, Profile Cards,
Profiles, media layouts, spotlight presentation, frames, backdrops and effects.
They use the existing Presentation Pack contract and contain data/art only.

The published 4.0.0 collection requires Presentation Contract 4 (Naut v0.0.39 or later) and preserves pack and component IDs. Earlier 3.0.0 (Contract 3, Naut v0.0.29 or later) and 2.1.0 archives remain available.

## Install and choose

1. Download a `.ntpack` from the collection page.
2. Open Settings → Presentation → Packs / Advanced → Install from file….
3. Select the downloaded pack and wait for validation.
4. Select Global Presets in Appearance to apply a complete compatible Contract 4 collection, or select Theme or Font independently. Select Home and Profile components in
   their Customize panels and select Card layouts in Customize Card. Save each
   selection. Home and Profile can save different Backdrops from the same pack.

To use the source folder instead, choose Install from folder… and select the
directory containing `pack.json`, for example `packs/celestial/` in a checkout.
An app rebuild is unnecessary. Merely copying a pack into the repository or
installation directory does not install it.

## Update, export and remove

A pack has its own stable `packId` and version. Install a newer download with
the same id and approve Replace. Validate the resulting appearance; retired
components may require choosing an alternative. Packs / Advanced also provides
Export and Remove. Removal resets bindings using the removed pack.

There is no automatic background pack update, online pack store or verified Official badge.
Imported packs are labeled User. Official collection membership is a maintainer
review decision. Review covers technical compatibility, provenance and fit for
the collection; it does not guarantee every appearance on every Profile.

## Authors and provenance

Each source pack includes license and provenance files. Font and other
third-party licenses remain independent of Naut's source license. Original
AI-assisted art is identified as such; provide source/permission details for any
reference material. Never include another person's photos, secrets or scripts.

Pack downloads are published under `/naut/packs/` on GitHub Pages, beside the
demo. They are separate from application Releases. In-app updates require a signed pack registry verified against the pinned publisher authority.

[Contribute your pack](contributing-packs.md).

### In-app official updates

The 4.0.0 collection and its signed registry are available on GitHub Pages. Manual installation from the collection page or source folders remains supported.

Settings → Presentation → Packs / Advanced exposes **Check official pack updates**, per-pack Update and Update all. Checks are explicit, not background polling. Only installed, usable official generations matching a signed predecessor version and directory content hash are offered; local modifications, private packs and standalone assets are excluded. The public Pages registry `checksums.json` must be accompanied by `checksums-signature.json`, signed with the existing pinned update publisher key. Download length and SHA-256, Contract compatibility, manifest identity and exact old/new content hashes are checked before transactional replacement by the existing pack store. Bindings retain their IDs; missing definitions follow the normal fallback policy. A failed replacement preserves the prior generation. The app sends only public registry and archive GET requests.

The source collection catalog retains predecessor version/content hashes. The 2.1.0 and 3.0.0 entries were derived from their curated source revisions and canonical archive file/text normalization. Keep previously published generation hashes when advancing a pack version. The packaging probe generates the new content hash and minimum contract in the registry; publication signs and publishes the registry, signature and archives in one Pages commit.

The 4.0.0 source collection includes a distinct Gallery architectural PNG and coordinated navigation/control, typography and media definitions for each world. Existing licensed art and stable component IDs are retained.

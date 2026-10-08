# Optional pack collection

[Browse and download official packs](../packs/README.md).
Packs can customize themes, fonts, controls, Home, Gallery, Profile Cards,
Profiles, media layouts, spotlight presentation, frames, backdrops and effects.
They use the existing Presentation Pack contract and contain data/art only.

## Install and choose

1. Download a `.ntpack` from the collection page.
2. Open Settings → Presentation → Packs / Advanced → Install from file….
3. Select the downloaded pack and wait for validation.
4. Select Theme/Font in Appearance and other components in their library or
   Home/Profile Customize screen. Save changes to commit a preview.

To use the source folder instead, choose Install from folder… and select the
directory containing `pack.json`, for example `packs/celestial/` in a checkout.
An app rebuild is unnecessary. Merely copying a pack into the repository or
installation directory does not install it.

## Update, export and remove

A pack has its own stable `packId` and version. Install a newer download with
the same id and approve Replace. Validate the resulting appearance; retired
components may require choosing an alternative. Packs / Advanced also provides
Export and Remove. Removal resets bindings using the removed pack.

There is no automatic pack update, online pack store or verified Official badge.
Imported packs are labeled User. Official collection membership is a maintainer
review decision. Review covers technical compatibility, provenance and fit for
the collection; it does not guarantee every appearance on every Profile.

## Authors and provenance

Each source pack includes license and provenance files. Font and other
third-party licenses remain independent of Naut's source license. Original
AI-assisted art is identified as such; provide source/permission details for any
reference material. Never include another person's photos, secrets or scripts.

Pack downloads are published under `/naut/packs/` on GitHub Pages, beside the
demo. They are separate from application Releases and signed update metadata.

[Contribute your pack](contributing-packs.md).

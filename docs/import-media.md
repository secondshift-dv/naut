# Import Media

Naut imports by **copying** media into the active Vault. Original files stay where they were.

## Ways to import

- Use **Import** to add files or folders and review the incoming items.
- Use **Add media** from a Profile to import directly for that Profile.

Both paths use the same media preparation pipeline; Profile-scoped import simply skips the step of choosing a destination Profile.

## Type-specific preparation

Naut does not send every media type through every processor. Depending on the input and its eligibility, the preparation pipeline can produce:

- metadata used by the catalog;
- thumbnails for browsing;
- bounded hover/banner presentation media for video;
- [local face analysis](face-intelligence.md) for applicable image/video content;
- durable Figure preparation for [GLB, FBX, OBJ, STL and 3MF models](model-formats.md).

A duplicate with identical content can reuse compatible preparation that already exists in the Vault instead of repeating unnecessary work.

The import review can suggest presentation media, but **Cover and Banner can always be changed later**.

## Imports, recovery and supported containers

**Add media** from a Profile imports eligible files directly into that Profile. The general **Import** surface still supports explicit review. Durable Import Activity records successes and exceptions; compatible exact duplicates can reuse existing content.

Transfers retain a staged copy and incremental SHA-256, with durable Resume and safe Cancel. Cancel settles an active copy at a checkpoint before cleaning interrupted staging; **Settings → Vault → Vault Health** can help resume, retry or finish interrupted cleanup. The copied originals in the Vault and original files outside the Vault are not silently removed by cancelling.

A transfer that finishes exactly at an already-durable checkpoint no longer repeats that final flush and catalog write. The 64 MiB checkpoint interval, staged copy, source integrity checks, and restart recovery remain unchanged.

New video imports prepare a bounded 480p/24 FPS HOVER rendition; Banner candidates can prepare a separate rendition of up to 900p/24 FPS. New Profiles may receive up to three initial Banner alternatives. Existing Profile imports preserve their current Banner collection and appearance. When storage contention is measured, background transfer yields adaptively without changing source bytes, SHA-256 integrity or durable checkpoints. These changes are not a claim of measured transfer-speed improvement.

Additional eligible inputs include JPEG 2000 (`.jp2`, `.j2k`), OGV/Theora, VOB and representative MXF containers. Actual support depends on the contained codec; damaged or unsupported media can still require attention. Video previews use prepared Thumbnail and Hover assets where supported.

## Originals and prepared media

Import remains a copy operation. The source file outside Naut is not cut or replaced. Prepared derivatives exist to make Naut responsive and presentation-aware; they are not destructive edits to the original file.

Opening an original media item still uses the normal Windows application associated with that file type.

## If an import needs attention

Open the Import surface and check **Needs attention**. Failed required preparation displays a safe technical reason and the affected capability/error code. Choose **Try again** to retry that item; keep the original file available until import has completed successfully.

Naut guards against video thumbnail timestamps beyond the last decodable frame and retries from the beginning when needed. A valid Hover or Banner rendition from the first attempt is retained. Damaged or unsupported video streams can still require your attention.

Face-analysis availability is separate from whether an image or video can belong to a Profile. Figure eligibility is likewise separate from normal Profile use.

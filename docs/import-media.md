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
- durable Figure preparation for supported 3D models.

A duplicate with identical content can reuse compatible preparation that already exists in the Vault instead of repeating unnecessary work.

The import review can suggest presentation media, but **Cover and Banner can always be changed later**.

## Originals and prepared media

Import remains a copy operation. The source file outside Naut is not cut or replaced. Prepared derivatives exist to make Naut responsive and presentation-aware; they are not destructive edits to the original file.

Opening an original media item still uses the normal Windows application associated with that file type.

## If an import needs attention

Open the Import surface and check **Needs attention**. Keep the original file available until the import has completed successfully.

Face-analysis availability is separate from whether an image or video can belong to a Profile. Figure eligibility is likewise separate from normal Profile use.

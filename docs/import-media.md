# Import Media

Naut imports by **copying** media into the active Vault. Original files stay where they were.

Managed copies use `ProfileName_type_ID.ext`. The type is `image`, `video`, or `model`. For a model with dependencies, the package directory uses `ProfileName_model_ID` and the files inside keep their original names and relative paths. Items in Trash use the current naming pattern when restored.

## Ways to import

- Use **Import** to add files or folders and review the incoming items.
- Use **Add media** from a Profile to import directly for that Profile.

Both paths use the same media preparation pipeline. Profile-scoped import uses that Profile as its destination. For a regular import, confirm the destination. When Review is needed, return after preparation and select **Add to Vault**.

## Type-specific preparation

Naut does not send every media type through every processor. Depending on the input and its eligibility, the preparation pipeline can produce:

- metadata used by the catalog;
- thumbnails for browsing;
- bounded hover/banner presentation media for video;
- [local face analysis](face-intelligence.md) for applicable image/video content;
- durable Figure preparation for [GLB, FBX, OBJ, STL and 3MF models](model-formats.md).

Naut skips exact duplicates already owned by the destination Profile. If every item is a duplicate, the import shows the skipped count and finishes without Review. Identical files in the same batch share one transfer when a valid representative is available. Profile Add media keeps independent publication per item while sharing eligible transfers within its session. A duplicate owned by a different Profile retains its original ownership. Only published managed media with valid ownership can suppress a new transfer.

The import review can suggest presentation media, but **Cover and Banner can always be changed later**.

## Imports, recovery and supported containers

After reopening Naut, select **Continue** in Import Activity to restore the saved choices. If the destination is missing or unavailable, select a Profile again before Review. Once Vault placement locks the destination, the batch follows its saved recovery steps.

Import Activity shows elapsed time during **Stage 1 – Preparing media** and Stage 2. The clock stops while preparation is paused or waiting for a retry. Duplicate checking uses the original admitted input total, including direct transfers. Batch summaries distinguish added, reused, duplicate-skipped, failed, cancelled and excluded items. Add media shows session totals without requiring Review for normal imports.

Transfers retain a staged copy and incremental SHA-256, with durable Resume and safe Cancel. Cancel settles an active copy at a checkpoint before cleaning interrupted staging; **Settings → Vault → Vault Health** can help resume, retry or finish interrupted cleanup. The copied originals in the Vault and original files outside the Vault are not silently removed by cancelling.

Transfers save a durable checkpoint every 64 MiB. A transfer can start while another job waits for its CPU or face-analysis slot. Background hashing, copying and media preparation share a budget for the underlying physical disk. When Windows reports contention, Naut yields briefly between chunks and gives background copy I/O a lower priority.

New video imports prepare a bounded 480p/24 FPS HOVER rendition; Banner candidates can prepare a separate rendition of up to 900p/24 FPS. New Profiles may receive up to three initial Banner alternatives. Existing Profile imports preserve their current Banner collection and appearance.

JPEG XL (`.jxl`) supports metadata, thumbnails, and preview. The Media Inspector can play supported JPEG XL animations. Gallery and Profile use the first frame for thumbnails. The original JPEG XL file stays in the Vault. Additional eligible inputs include JPEG 2000 (`.jp2`, `.j2k`), OGV/Theora, VOB and representative MXF containers. Actual support depends on the contained codec; damaged or unsupported media can still require attention. Video previews use prepared Thumbnail and Hover assets where supported.

## Originals and prepared media

Import remains a copy operation. The source file outside Naut is not cut or replaced. Prepared derivatives exist to make Naut responsive and presentation-aware; they are not destructive edits to the original file.

Opening an original media item still uses the normal Windows application associated with that file type.

## If an import needs attention

Open the Import surface and check **Needs attention**. Failed required preparation displays a safe technical reason and the affected capability/error code. Choose **Try again** to retry that item; keep the original file available until import has completed successfully.

Naut guards against video thumbnail timestamps beyond the last decodable frame and retries from the beginning when needed. A valid Hover or Banner rendition from the first attempt is retained. Damaged or unsupported video streams can still require your attention.

Windows icons (.ico) can be imported as images. If the face-analysis decoder cannot read a file, the import can continue once the required checks pass. To resume a batch already held at Stage 1 by an icon in an older Naut version, update Naut and select **Retry** in Import Activity. Figure eligibility is likewise separate from normal Profile use.

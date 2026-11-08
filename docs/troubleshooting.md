# FAQ & Troubleshooting

## Does Naut upload my Vault?

Naut is designed as a local-first application. The Vault is a local folder selected by the user.

## Does import move my original files?

No. Import copies media into the Vault. Originals remain at their original paths.

## Can I change Cover or Banner after import?

Yes. Import suggestions are not permanent presentation decisions.

## Why is there no face suggestion for a media item?

Face analysis is type-specific and depends on applicable content, usable detections, model availability, compatible embeddings, and an identity index with confirmed samples. A media item can still belong to a Profile even when face analysis produces no candidate.

See [Face Intelligence](face-intelligence.md) for the detection → embedding → candidate → confirmation distinction.

## Does a face candidate automatically identify someone?

No. A candidate is a reviewable model suggestion. Naut keeps confirmation as a separate durable decision.

## MPG or MPEG stays at Preparing media

Current builds no longer wait for assets created only after candidate confirmation. Open Import and check **Review** for completed candidates or **Needs attention** for failed required work. Keep the original files available for retry or recovery. If the import was cancelled, import the originals again.

## Why does an original video open outside Naut?

Opening an original media item uses the Windows default application for that file. For original video playback, the available Windows player/codecs therefore matter.

## Do I need a discrete GPU?

No. Integrated graphics are sufficient for normal Naut use. Interactive 3D Figure is an optional Direct3D 11 workload; stronger graphics hardware mainly adds headroom for more complex models.

## A Figure is unavailable. Can I still use the Profile?

Yes. Figure is optional and independent from the rest of the Profile experience.

Naut v0.0.11 prevents a temporary Profile layout reflow from canceling an initial 3D scene load. If the prepared model, compatible graphics device or renderer is unavailable, the static thumbnail remains the fallback.

Naut v0.0.12 also prevents Figure layout collisions with Profile identity and controls in narrow windows and with optional layouts.

## Why does About not immediately show a release date?

About retrieves the published date for **the installed Naut version** from the official GitHub Release. Keep Internet access available for the first lookup; opening About again retries a failed request. Once fetched, the date is cached locally in AppState and remains visible offline. Naut does not guess an unverified publication date or use a different version to fill the field.


## Which interface languages are included?

Naut includes English, Bahasa Indonesia, 日本語, 한국어, 简体中文, Deutsch, Français, and Español. See [Languages](languages.md).

## Where should I put the Vault?

Choose a normal folder you control with enough space for the collection. Keep the Vault outside the extracted Naut application and AppData folders, including their parent folders. If first-run setup reports an installation/AppData overlap, create a separate sibling folder instead (for example, `D:\Apps\NautVault` next to `D:\Apps\Naut`). Never delete an existing Vault to resolve this message.

## Can I move to a different PC?

Keep a complete copy of the Vault together. Install/extract Naut separately on the destination PC, then use Naut's Vault selection flow to open the compatible Vault.

## What if an import is interrupted?

Open **Import → Activity / Needs attention** to review exceptional items. A cancelled transfer is settled at a safe checkpoint before staging cleanup. If cleanup or recovery remains incomplete, use **Settings → Vault → Vault Health** to resume, retry or finish the pending operation. Retain the original external files until import has succeeded.

## Can I use an older Naut build after a Vault schema upgrade?

Naut can upgrade recognized Catalog v1 and v2 Vaults to schema v3, preserving existing collection data. Older builds may reject the upgraded database or be unable to write it safely. To downgrade, restore a complete pre-upgrade Vault backup; do not replace individual catalog files independently. See [Vault compatibility](vault.md#compatibility-and-text-files).

## Why does a Profile Overview or Private Note report an external edit?

Managed NORMAL Profiles can mirror these fields to readable UTF-8 files inside the Vault. A change made outside Naut is not silently accepted; review the conflict in the application before choosing which content to keep. These files are plaintext and should be protected as part of the Vault.

## How are updates delivered?

Official builds use signed update metadata from the official `secondshift-dv/naut` GitHub Releases channel. Download releases only from the official repository.

## Something still fails

Check the current [GitHub Issues](https://github.com/secondshift-dv/naut/issues) before filing a new bug report. For security-sensitive findings, use the repository's private vulnerability reporting rather than a public issue.

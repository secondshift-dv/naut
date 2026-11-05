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

Naut v0.0.4 fixes candidate imports that had finished metadata and face-analysis jobs but kept waiting for assets created only after confirmation. Update to v0.0.4 and open Import again. A completed candidate can offer **Review**; imports with failed required work remain under **Needs attention**. If you already cancelled the import, import the original files again.

## Why does an original video open outside Naut?

Opening an original media item uses the Windows default application for that file. For original video playback, the available Windows player/codecs therefore matter.

## Do I need a discrete GPU?

No. Integrated graphics are sufficient for normal Naut use. Interactive 3D Figure is an optional Direct3D 11 workload; stronger graphics hardware mainly adds headroom for more complex models.

## A Figure is unavailable. Can I still use the Profile?

Yes. Figure is optional and independent from the rest of the Profile experience.

## Which interface languages are included?

Naut v0.0.7 includes English, Bahasa Indonesia, 日本語, 한국어, 简体中文, Deutsch, Français, and Español. See [Languages](languages.md).

## Where should I put the Vault?

Choose a normal folder you control with enough space for the collection. Keep the Vault outside the extracted Naut application and AppData folders, including their parent folders. If first-run setup reports an installation/AppData overlap, create a separate sibling folder instead (for example, `D:\Apps\NautVault` next to `D:\Apps\Naut`). Never delete an existing Vault to resolve this message.

## Can I move to a different PC?

Keep a complete copy of the Vault together. Install/extract Naut separately on the destination PC, then use Naut's Vault selection flow to open the compatible Vault.

## What if an import is interrupted?

Open **Import → Activity / Needs attention** to review exceptional items. A cancelled transfer is settled at a safe checkpoint before staging cleanup. If cleanup or recovery remains incomplete, use **Settings → Vault → Vault Health** to resume, retry or finish the pending operation. Retain the original external files until import has succeeded.

## Can I use an older Naut build after opening a Vault in v0.0.7?

A recognized Catalog Schema v1 Vault is upgraded to v2 on first compatible open. Older v1 binaries reject that upgraded database; restore a complete pre-upgrade Vault backup if you must downgrade. Do not replace individual catalog files independently.

## Why does a Profile Overview or Private Note report an external edit?

Managed NORMAL Profiles can mirror these fields to readable UTF-8 files inside the Vault. A change made outside Naut is not silently accepted; review the conflict in the application before choosing which content to keep. These files are plaintext and should be protected as part of the Vault.

## How are updates delivered?

Official builds use signed update metadata from the official `secondshift-dv/naut` GitHub Releases channel. Download releases only from the official repository.

## Something still fails

Check the current [GitHub Issues](https://github.com/secondshift-dv/naut/issues) before filing a new bug report. For security-sensitive findings, use the repository's private vulnerability reporting rather than a public issue.

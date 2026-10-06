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

## Why does an original video open outside Naut?

Opening an original media item uses the Windows default application for that file. For original video playback, the available Windows player/codecs therefore matter.

## Do I need a discrete GPU?

No. Integrated graphics are sufficient for normal Naut use. Interactive 3D Figure is an optional Direct3D 11 workload; stronger graphics hardware mainly adds headroom for more complex models.

## A Figure is unavailable. Can I still use the Profile?

Yes. Figure is optional and independent from the rest of the Profile experience.

## Which interface languages are included?

Naut v0.0.2 includes English, Bahasa Indonesia, 日本語, 한국어, 简体中文, Deutsch, Français, and Español. See [Languages](languages.md).

## Where should I put the Vault?

Choose a normal folder you control with enough space for the collection. Keep the Vault separate from the extracted Naut application directory.

## Can I move to a different PC?

Keep a complete copy of the Vault together. Install/extract Naut separately on the destination PC, then use Naut's Vault selection flow to open the compatible Vault.

## How are updates delivered?

Official builds use signed update metadata from the official `secondshift-dv/naut` GitHub Releases channel. Download releases only from the official repository.

## Something still fails

Check the current [GitHub Issues](https://github.com/secondshift-dv/naut/issues) before filing a new bug report. For security-sensitive findings, use the repository's private vulnerability reporting rather than a public issue.

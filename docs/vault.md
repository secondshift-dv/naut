# Vault

A **Vault** is the local folder that holds the durable Naut collection. It is separate from the portable Naut application folder.

## Local-first storage

Naut keeps the authoritative catalog and durable collection data locally in the selected Vault. The Vault contains the collection boundary: catalog state, copied media, and durable prepared material needed by Naut.

Prepared derivatives and caches support presentation and performance, but they do not turn the application installation folder into the source of truth.

## Choosing a Vault

On first launch, choose a folder outside the application installation and AppData folders. A separate sibling folder is supported, but selecting the installation folder, AppData folder, or their ancestors/descendants is not allowed. Choose an existing empty folder for a new Vault or an existing Naut Vault to reopen it. Naut does not require a separate name field: the chosen folder is the location and its folder name is the Vault name.

You can later pick an existing compatible Vault or move the active Vault through the supported Vault controls.

## Media intake

Imports copy source files into the Vault. Original files outside the Vault are left in place.

This is why the portable app package and the Vault can be handled separately: replacing or updating the application does not mean replacing the collection.

## Name and icon per Vault

Use **Settings → Vault** to rename the active Vault or choose a custom icon (PNG, JPEG or WebP, up to 8 MB). Each Vault stores its icon inside its own folder; switching, moving or reopening Vaults restores their individual identities. Resetting the icon affects only that Vault.

## Compatibility and text files

Naut v0.0.7 upgrades recognized Catalog Schema v1 Vaults atomically to v2 without resetting existing rows. Older v1 binaries cannot open an upgraded catalog; back up the Vault before upgrading if you may need to downgrade.

Naut v0.0.9 atomically advances recognized Catalog v1/v2 Vaults to schema v3, preserving existing media identities, Profile selections and Banner membership. Profile manifests use schema 2 while compatible readers still accept schema 1. Older Naut versions cannot safely write an upgraded v3 Catalog. For a downgrade, restore a complete pre-upgrade Vault backup; do not delete or reset the upgraded Catalog.

Managed NORMAL Profiles can mirror Overview and Private Note into UTF-8 text files inside the Vault. These files are **plaintext**; anyone with filesystem access can read them. If edited outside Naut, the application asks how to resolve the conflict before adopting or replacing the content. Keep the entire Vault together when backing it up or moving it.

## Backups

For a simple offline backup, close Naut first and copy the entire Vault folder as one unit. Keep the internal structure intact rather than copying selected database or derivative files individually.

## Application files are separate

Do not treat the extracted Naut application directory as the Vault. The application package contains the executable, runtime, and release metadata; the Vault is the durable collection boundary.

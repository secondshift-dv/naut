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

When you first open a Vault that still uses the older naming format, Naut shows progress while it renames its managed originals. Your Cover, Banner, Figure 3D, and Profile associations carry over. A larger collection can take longer on that first open. If a Vault file is in use, close the app using it and retry. If an original is missing, restore it to its recorded location from your Vault backup and retry.

This is why the portable app package and the Vault can be handled separately: replacing or updating the application does not mean replacing the collection.

## Name and icon per Vault

Use **Settings → Vault** to rename the active Vault or choose a custom icon (PNG, JPEG or WebP, up to 8 MB). Each Vault stores its icon inside its own folder; switching, moving or reopening Vaults restores their individual identities. Resetting the icon affects only that Vault.

<a id="compatibility-and-text-files"></a>

## Catalog compatibility and upgrades

Naut v0.0.39 and later use Catalog Schema 4. Opening a recognized older Vault automatically applies the necessary migrations in order; each transition commits atomically and preserves existing collection identities, associations and presentation selections. Unknown or incompatible catalogs are rejected, not reset.

| Catalog transition | Introduced in |
| --- | --- |
| Schema 1 to 2 | [v0.0.7](releases/v0.0.7.md) |
| Schema 2 to 3 | [v0.0.9](releases/v0.0.9.md) |
| Schema 3 to 4 (canonical-original health) | [v0.0.39](releases/v0.0.39.md) |

Profile manifests use schema 2 and retain schema 1 read compatibility; their schema is separate from the catalog.

Close Naut and back up the entire Vault before opening it with a newer version. Older binaries reject catalogs upgraded beyond their supported schema. To downgrade, restore a complete pre-upgrade Vault backup; never delete or reset the catalog to bypass compatibility checks.

## Profile text files

Managed NORMAL Profiles can mirror Overview and Private Note into UTF-8 text files inside the Vault. These files are **plaintext**; anyone with filesystem access can read them. If edited outside Naut, the application asks how to resolve the conflict before adopting or replacing the content. Keep the entire Vault together when backing it up or moving it.

## Review original integrity

In **Settings > Vault > Vault Health**, Standard checks placement and file length; Deep also verifies SHA-256, complete model packages, SQLite integrity and foreign keys. A scan is diagnostic. Confirm an original-integrity reconciliation to record missing or mismatched originals in the catalog and exclude them from exact duplicate reuse. A recorded exclusion persists across restarts. Unrecorded originals are not certified healthy.

When available, choose Restore original to supply a matching source file (or model-package folder). Naut verifies the original fingerprint and preserves both the selected source and any displaced damaged bytes. It can also reconcile an unambiguous verified canonical placement. These actions preserve Media identity, associations and appearance. Known invalid originals must be recovered or reverified before new Trash operations. Do not delete the catalog or reset its fingerprints to repair corruption.

## Review Profile text findings

Run **Check Vault** in **Settings → Vault → Vault Health** to identify the Profile and file associated with each text finding. **Open Profile** takes you to that Profile, where **Review changed text files** lets you inspect the file, restore catalog text, or review a pending imported overview as appropriate.

## Backups

For a simple offline backup, close Naut first and copy the entire Vault folder as one unit. Keep the internal structure intact rather than copying selected database or derivative files individually.

## Application files are separate

Do not treat the extracted Naut application directory as the Vault. The application package contains the executable, runtime, and release metadata; the Vault is the durable collection boundary.

# Vault

A **Vault** is the local folder that holds the durable Naut collection. It is separate from the portable Naut application folder.

## Local-first storage

Naut keeps the authoritative catalog and durable collection data locally in the selected Vault. The Vault contains the collection boundary: catalog state, copied media, and durable prepared material needed by Naut.

Prepared derivatives and caches support presentation and performance, but they do not turn the application installation folder into the source of truth.

## Choosing a Vault

On first launch, choose the folder where the Vault should live. Naut does not require a separate name field: the chosen folder is the location and its folder name is the Vault name.

You can later pick an existing compatible Vault or move the active Vault through the supported Vault controls.

## Media intake

Imports copy source files into the Vault. Original files outside the Vault are left in place.

This is why the portable app package and the Vault can be handled separately: replacing or updating the application does not mean replacing the collection.

## Backups

For a simple offline backup, close Naut first and copy the entire Vault folder as one unit. Keep the internal structure intact rather than copying selected database or derivative files individually.

## Application files are separate

Do not treat the extracted Naut application directory as the Vault. The application package contains the executable, runtime, and release metadata; the Vault is the durable collection boundary.

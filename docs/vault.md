# Vault

A **Vault** is the local folder that holds the durable Naut collection. It is separate from the portable Naut application folder.

## Local-first storage

Naut keeps the authoritative catalog and durable collection data locally in the selected Vault. Prepared derivatives and caches support presentation and performance, but the Vault remains the user's collection boundary.

## Choosing a Vault

On first launch, choose the folder where the Vault should live. Naut does not require a separate name field: the chosen folder is the location and its folder name is the Vault name.

You can later pick an existing compatible Vault or move the active Vault through the supported Vault controls.

## Media intake

Imports copy source files into the Vault. Original files outside the Vault are left in place.

## Backups

For a simple offline backup, close Naut first and copy the entire Vault folder as one unit. Keep the internal structure intact rather than copying selected database or derivative files individually.

## Application files are separate

Do not treat the extracted Naut application directory as the Vault. This separation lets the application package be replaced by an update without replacing the user's collection.

# Getting Started

## 1. Download Naut

Download **Naut v0.0.35** from the official [GitHub Releases](https://github.com/secondshift-dv/naut/releases/tag/v0.0.35) page.

The Windows package is portable. Extract the complete ZIP into a new folder before running Naut.

## 2. Start Naut

Run `naut.exe`.

Keep `runtime/` and `release-manifest.json` beside `naut.exe`. Check the Naut product version in **Settings → About**. Windows file properties for the top-level `naut.exe` show the launcher's component version.

## 3. Choose where to keep your Vault

On first launch Naut asks **where to keep** the Vault. Pick a folder you control. The selected folder becomes the Vault location and its folder name is used as the Vault name.

A Vault must be outside the Naut application and AppData folders (including their parent folders). For example, if Naut is installed in `D:\Apps\Naut`, use `D:\Apps\NautVault` as a separate sibling folder. Your collection should not depend on where the application itself is extracted.

## 4. Import media

Use **Import** or **Add media** from a Profile. Naut copies selected files into the Vault; it does not cut or move the originals.

Images, videos, and supported 3D model media receive the preparation appropriate to their type. Cover and Banner choices can be changed later.

Review explains which item needs attention and lets you remove it while keeping the remaining media. Importing a Vault root takes original media from its Profiles and skips derivatives, system files and Trash. A nonempty `overview.txt` in an imported Profile folder is offered for review before replacing an Overview.

## 5. Explore

- **Home** surfaces featured Profiles and recent activity.
- **Gallery** browses and filters Profiles.
- **Profile** brings identity, media, notes, relationships, and optional 3D Figure presentation together.
- **Settings** controls appearance, language, presentation resources, Vault tools, activity, and Trash.

You can rename the current Vault and give it an independent icon through **Settings → Vault**. Those preferences belong to the Vault, not the extracted application folder.

For a browser-based preview without touching local files, open the [Naut Interactive Showcase](https://secondshift-dv.github.io/naut/). For update details and compatibility notes, read the [v0.0.35 release notes](releases/v0.0.35.md).

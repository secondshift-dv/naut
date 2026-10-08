# Contribute an optional pack

Contribute customization content without changing Naut's application code.
Fork the public [naut repository](https://github.com/secondshift-dv/naut), add a
focused pack contribution, and open a Pull Request against `main`. Maintainers
review and curate acceptance. You do not need access to private naut-dv.

## Source structure

Create `packs/<slug>/` containing:

- `pack.json`: stable pack id, version, name, author, explicit license and contract version;
- `assets/`: only assets needed by declared definitions;
- `licenses/`: notices for fonts and any third-party content;
- `LICENSE.txt`: the license applicable to original pack content;
- `PROVENANCE.txt`: creator, source, permissions, modifications and AI/reference disclosures;
- `README.txt` or `README.md`: installation and recommended component combinations;
- `preview.png`: a readable preview with no private Profile media.

Add a matching entry to `packs/catalog.json` and a card to `packs/README.md`.
Use a unique lowercase slug and a namespaced id such as `yourname.night-garden`;
definition ids begin with that id plus a dot. Keep ids stable during updates,
increase the pack version when content changes, and describe removed components.
New submissions may copy `packs/celestial/` as a structural reference, replacing
its ids, artwork and license/provenance with their own contribution.

## Review and evidence

Import from folder in Naut. Exercise the components you changed, including
readability, alignment, clipping, transparent frame openings and reduced motion
where applicable. Provide screenshots using your own media or neutral content;
identify mockups separately from screenshots. Technical checks do not replace
visual review. Submit only content you own or have permission to distribute.
Clearly identify original AI-assisted artwork and any licensed references.

On Windows with the repository's .NET SDK, run:

```powershell
pwsh ./scripts/verify.ps1 -Scope Packs
```

This validates catalog/manifest identity, assets, the real Naut compiler,
archive installation, resolved plans, replacement, restart loading and export.
The GitHub pack check uses read-only permissions on fork PRs. It does not publish
downloads. Do not commit `.ntpack`/ZIP output, build artifacts or local Vault data.

## Maintainer acceptance and publication

Public pack-only contributions can remain public community content. Any needed
engine changes are adopted in naut-dv first. Maintainer-authored product work
starts in naut-dv and promotes a verified snapshot into public naut.

After review, merge accepted source and validate the final public `main`.
`package-packs.ps1` creates archives and a checksum catalog under `out/`.
`publish-packs.ps1 -Publish` publishes only those files into the existing public
`gh-pages` branch, preserving the live demo. It refuses a changed archive at an
already published pack version and never creates an application release/tag.

```powershell
pwsh ./scripts/utilities/package-packs.ps1
pwsh ./scripts/utilities/publish-packs.ps1 -Publish
```

Only a maintainer with public repository write access publishes. There is no
automatic acceptance or publication from a contributor PR. Accepted sources,
preview, author credit and notices remain visible in the repository.

For definition details, see the Presentation Pack reference under the
repository's development documentation. Public: [pack reference](https://github.com/secondshift-dv/naut/blob/main/docs/development/presentation-packs.md).

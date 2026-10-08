## Summary

Describe the change and why it belongs in Naut.

## Risk

- [ ] Low — README, translations, public docs, ordinary copy, known-safe examples
- [ ] Medium — ordinary UI, presentation runtime, non-persistence source, public build configuration
- [ ] High — database/schema, Vault, Import, Updater, release/signing, security, native renderer, ModelRender, persistence, package layout, third-party runtime, or licensing-sensitive assets

High-risk changes require maintainer review and are never auto-merged.

## Validation

- [ ] Relevant checks pass: Packs for pack-only work; Source for application changes
- [ ] I added relevant validation for changed behavior
- [ ] I did not include generated output, secrets, private data, or unknown-provenance assets
- [ ] Third-party content or dependency changes include source, license, notices, and provenance as applicable

## Authority

Public core changes are reviewed here, then adopted/reconciled in private `naut-dv` before an authoritative public update. Public-only documentation, translations, and community content may remain public-only.

## Optional pack contribution (when applicable)

- [ ] Pack source is under `packs/<slug>/` and the catalog/collection card match
- [ ] Stable ids and a new version for changed published content
- [ ] Installation guide, preview, license and provenance are included
- [ ] `pwsh ./scripts/verify.ps1 -Scope Packs` passes
- [ ] I tested the changed components in Naut; mockups are identified separately
- [ ] No personal media, scripts or generated download archives are included

Pack-only contributions use the Packs check; application Source/Build checks
are required only when application code or packaging is changed.

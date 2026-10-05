## Summary

Describe the change and why it belongs in Naut.

## Risk

- [ ] Low — README, translations, public docs, ordinary copy, known-safe examples
- [ ] Medium — ordinary UI, presentation runtime, non-persistence source, public build configuration
- [ ] High — database/schema, Vault, Import, Updater, release/signing, security, native renderer, ModelRender, persistence, package layout, third-party runtime, or licensing-sensitive assets

High-risk changes require maintainer review and are never auto-merged.

## Validation

- [ ] `pwsh ./scripts/verify.ps1 -Scope Source` passes
- [ ] I added relevant validation for changed behavior
- [ ] I did not include generated output, secrets, private data, or unknown-provenance assets
- [ ] Third-party content or dependency changes include source, license, notices, and provenance as applicable

## Authority

Public core changes are reviewed here, then adopted/reconciled in private `naut-dv` before an authoritative public update. Public-only documentation, translations, and community content may remain public-only.

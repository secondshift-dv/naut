# Release notes policy

Every Naut release must explain what changes for the user. A generated commit list or a Full Changelog link alone is insufficient.

The reviewed source of the release body is `docs/releases/v<ProductVersion>.md`. The version must match `Directory.Build.props`. Write this file in the development repository and promote it with the approved release snapshot. Use English for the public release; optional translations may follow the English copy.

Required structure:

- `# Naut vX.Y.Z`: the exact product version.
- `## Highlights`: concise bullets describing actual additions, fixes, or changes and their practical effect. Include a before/after example when it makes a fix clearer.
- `## Download and update`: the version-specific Windows x64 ZIP link and clear instructions for new and existing users.
- `## Notes`: relevant prerequisites, compatibility changes, limitations, or known issues. State when no additional requirements apply.

A Full Changelog comparison link may follow these sections as supporting detail. Do not use commit titles as a substitute for user-facing explanations. Avoid unsupported claims, unfinished placeholders, internal implementation history, or promises about unverified behavior.

Before releasing:

1. Prepare and review the versioned notes alongside the product changes.
2. Run `pwsh ./scripts/verification/verify-release-notes.ps1`. Check the claims against fresh verification evidence; the structural check does not prove their accuracy.
3. Use the canonical release workflow. Both draft creation and publication require valid notes; an existing draft receives the reviewed body again.
4. Confirm the uploaded draft body matches the committed notes before publishing.

The release script validates the notes before building or signing and passes them through GitHub CLI's `--notes-file`. The public workflow also validates them before building. It checks version, required populated sections, a highlight bullet, the exact ZIP link, and unfinished placeholders. Missing or invalid notes block release creation. Signature, payload validation, source provenance, and published-release immutability remain governed by the existing release contract.

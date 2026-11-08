# Contributing to Naut

Discuss intent in an issue, then submit a focused pull request with provenance and relevant validation. README, translations, public documentation and community examples may be maintained here.

Core changes are reviewed here, accepted by the maintainer, adopted and reconciled in private naut-dv, verified there, and published back as curated source. Public Naut is not a second engineering authority. Automated checks establish technical validity; maintainers decide whether a change belongs in Naut.

By submitting a contribution, you confirm that:

- You own the contribution or have sufficient rights and permission to submit it.
- You grant Naut's maintainer a nonexclusive, worldwide, royalty-free, irrevocable license to incorporate, reproduce, modify and distribute your contribution as part of official Naut under the repository's licensing terms.
- Your contribution follows the applicable repository license, including PolyForm Shield 1.0.0 for Naut-owned source.
- You disclose third-party content, its authors, source, license and any modifications, and provide provenance when required.
- You submit no secrets, private user data, generated output or material with unknown redistribution rights.

This contribution grant does not transfer your ownership or replace third-party licenses. No paid CLA service is required.

Run `pwsh ./scripts/verify.ps1 -Scope Source` for source changes. Use `pwsh ./scripts/build.ps1` when packaging is affected. Builds require Windows x64 and .NET SDK 10.0.401. Do not test on another person's Vault or disclose personal media in a pull request.

README, translations and ordinary public copy are usually low risk. Ordinary UI, presentation runtime and build configuration are usually medium risk. Persistence, schema, Vault, Import, Updater, release/signing, security, the native renderer, ModelRender, runtime dependencies, package layout and licensed assets are high risk. High-risk changes require maintainer review and never auto-merge.

Report security vulnerabilities privately using the repository's Security reporting surface rather than a public issue.

## Documentation integrity

Public README pages, translations, guides, release notes and pack previews must describe the currently published version, not a development-only or retired definition. Keep the active download and versioned links aligned with `Directory.Build.props`; keep pack display names and counts aligned with `packs/catalog.json` and the shipped `pack.json`. Historical release notes remain archival and must not be rewritten to present them as current behavior.

Use UTF-8 throughout. Do not substitute a bare `?` for punctuation or publish repeated `?` placeholders, damaged replacement characters, control bytes or mojibake. Legitimate questions, URL query syntax and code examples can retain their question marks. Keep relative links valid, preserve attribution, and display only shipped pack components in documentation artwork. Treat static compositions as illustrations, never as verified in-app screenshots.

Run `pwsh ./scripts/verification/verify-documentation.ps1` after documentation edits; it also runs during `pwsh ./scripts/verify.ps1 -Scope Source`. The automated guard checks encoding, suspicious punctuation, links, versioned downloads and pack identity; feature accuracy still requires comparing documentation with implementation and release evidence.

## Release notes

Product releases require reviewed, versioned user-facing notes. Follow the [Release notes policy](docs/release-notes.md) and run `pwsh ./scripts/verification/verify-release-notes.ps1` before release preparation. A Full Changelog link alone is insufficient.

## Optional pack contributions

Customization packs have their own source collection, licenses and versions.
For a pack-only PR, follow [Contribute a pack](docs/contributing-packs.md) and
run `pwsh ./scripts/verify.ps1 -Scope Packs`. Submit source art/definitions and
preview/provenance, not generated archives. Maintainer review determines
official collection membership; a successful check never auto-publishes a pack.

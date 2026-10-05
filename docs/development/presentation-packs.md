# Presentation Packs

Presentation customization has one transport and validation authority: a **Presentation Pack** with a `pack.json` manifest. Do not add a second standalone-theme/layout/frame loader. A pack may contain **one definition only**, so a single custom component stays lightweight while using the same validation, persistence, replacement, export, and removal path as larger packs.

Presentation Contract v1 keeps manifest and binding schemas at version 1. **Settings -> Appearance** intentionally exposes only Theme and Font. Control Styles live in **Settings -> Presentation** with the rest of the reusable presentation library. Presentation separates directly importable Fonts, Frames and Backdrops from Effects, Control Styles and Packs / Advanced; everyday library cards include a compact preview plus Default/Built-in/User provenance tags, while pack transport, export/removal, raw identifiers, counts, and diagnostics stay under **Packs / Advanced**. Icons, commands, selection, focus, queries, media readiness, accessibility, and ResourceGovernor admission remain engine-owned.

The built-in customization UX exposes Home Effect, Profile Effect and independent Card Effect decisions. Gallery has no surface effect picker and Settings has no Effect picker. Normal built-in choices are None, Bubbles, Rain, Snow and Silk, plus compatible definitions installed by the user. Profile Effect and Card Effect are independently Profile-scoped; Home Effect is surface-scoped. No target inherits another target's effect. The detailed engine slots remain intact for Presentation Pack compatibility. **Save changes** commits the existing preview transaction; Cancel asks before discarding staged changes.

## Minimal component workflow

1. Copy `examples/presentation-pack-minimal/` to a working folder.
2. Give `packId` a stable lowercase id such as `myname.gallery-layout`.
3. Every definition id must begin with that pack id plus a dot, for example `myname.gallery-layout.grid`.
4. Keep only the definition(s) you need. `assets` may be empty.
5. In **Settings → Presentation → Packs / Advanced**, choose **Install from folder…** and select the folder containing `pack.json`.
6. Choose the installed definition from the normal customization surface for its kind. Bindings persist through the existing presentation binding authority.
7. To update the same pack id, install it again and approve **Replace**. The replacement is validated before becoming authoritative.
8. Installed user packs can be **Exported** to a portable `.ntpack` and **Removed** from the same screen.

Fonts accept `.ttf`/`.otf`; Backdrops accept `.png`, `.jpg`, `.jpeg`, `.webp` or `.mp4`; direct Frames accept square transparent `.png`/`.webp` artwork from 256 to 4096 px. Effects and Control Styles can be added as supported declarative pack definitions.

A `.zip` or `.ntpack` containing `pack.json` is also accepted. An archive may wrap the pack in one top-level directory.

## Direct custom frame workflow

**Settings → Presentation → Frames → Add custom frame…** accepts a square transparent `.png` or `.webp` between 256×256 and 4096×4096 pixels. The center of the artwork must be transparent so the Profile Cover remains visible. Direct custom frames use the same square stage for the Cover and frame artwork: naut renders the Cover first at the full frame bounds, then composites the transparent frame PNG above it, so there is no artificial gap between the Cover and decorative artwork.

This direct intake is an authoring shortcut, not a second frame authority. naut validates the artwork, builds a one-definition user Presentation Pack in temporary staging, installs it through the normal pack validator/compiler/store, and makes that `cover-frame` definition available in Profile Customize. Installation does not select it for a Profile; selection uses the normal preview transaction and **Save changes** action. Installed custom frames remain pack-backed and are exported or removed from **Packs / Advanced** like any other user pack.

Direct image intake is intentionally static. More advanced custom frame behavior remains declarative Presentation Pack territory so motion/effects continue to pass through the same bounded renderer and performance authority instead of introducing executable or arbitrary shader content.

## Manifest contract

The current pack schema is `1`; the current Presentation Contract version is `1`. A minimal manifest contains:

- `schemaVersion`, `packId`, `version`, `name`, and `minContractVersion`;
- optional `author`, `description`, and `license`;
- `assets`, which may be an empty array;
- `definitions`, with one or more declarative definitions.

Pack and definition ids use lowercase letters, digits, `.`, and `-`. A definition id must start with `<packId>.`.

Supported definition kinds are:

`theme`, `home-layout`, `backdrop`, `spotlight-style`, `gallery-layout`, `profile-card`, `profile-layout`, `cover-frame`, `profile-media-layout`, `typography`, `control-skin`, `effect`.

The `spec` object is kind-specific and is compiled by the existing Presentation compiler. Use a built-in definition of the same kind as the reference shape; do not bypass the compiler by adding ad-hoc runtime properties. A `gallery-layout` is only the virtualized Gallery shell: its collection primitive, item envelope, spacing and column/row limits control arrangement. It never selects or replaces `profile-card`; each Profile keeps its own card definition inside that shell. Gallery cards always receive the same Profile identity data (name, category, tags, favorite state, and rating); the Profile Card composition decides where those values appear.

## Assets and safety

Assets are optional. When used, they are declared by id, kind, and relative path and are resolved only through the pack asset store. Installation stages the selected folder/archive, validates containment, manifest fields, assets, definition kinds, and compiler semantics, then moves the validated pack into the user presentation-pack authority.

Profile-card `hover` accepts `lift`, `sheen`, `spotlight`, `banner` or `none`. `banner` preserves the Cover composition at rest and hosts the selected ready Banner hover video inside its artwork area, as used by Editorial. Installed user packs appear after the built-in library in Packs / Advanced, and their supported components appear in the corresponding library tabs.

`hover: banner` requires exactly one Banner image or one non-ambient Cover image in the composition. Frame-only, text-only, ambient-Cover-only, and multiple-host compositions are rejected. Imported Backdrops are offered in Home and Profile Customize according to their `home-only` or `profile-only` tags; omission makes them available to both targets.

Presentation packs are data/art only. Executables, scripts, source files, shaders, links, SQL, XAML, and other forbidden content are rejected. Pack installation is separate from media import and does not turn presentation assets into Vault media.

Manifests are limited to 4 MiB and malformed field types produce validation diagnostics. Folder intake checks the selected root and traversed directories for links or reparse points. Images must decode completely within a 64 MiB pixel budget. Video installation requires approved packaged tools to inspect a video stream and decode its first frame with bounded execution; header bytes alone cannot establish a usable video. Unavailable tools reject video intake with a diagnostic.

Replacement preserves the prior generation until catalog publication succeeds. Install and removal record an operation journal before moving a directory; startup reconciles interrupted operations against the catalog's recorded content hash before loading packs. Recovery verifies generation hashes and containment before cleanup and preserves changed or ambiguous bytes. Committed cleanup records each file before deletion so a locked file or interrupted partial cleanup can be retried without blocking the active pack. A remaining operation blocks another change to that pack until recovery settles it. These operation receipts are recovery metadata, not a second selection authority.

Pack definitions consume the selected Profile Cover and Banner MediaAssets through their composition rules. A layout or theme change does not compile, trim, or transcode media. Fit, crop, and focal placement are rendering choices.

The Profile Figure is an engine-owned use of canonical Model Media and its prepared ModelRender derivative. It is part of the shared identity header, not a Presentation Pack definition kind, slot or body module. Profile layouts may position the identity semantic region, but cannot force a Figure source, replace its renderer, introduce shaders or bypass its exclusive native-viewport/resource policy. Home, Gallery and media cards retain static model thumbnails.

## Repository example

`examples/presentation-pack-minimal/pack.json` is intentionally a one-definition pack. It demonstrates that “custom layout intake” does not require a large bundle or a separate file format.

## Custom theme format

`examples/presentation-pack-theme/pack.json` is a complete one-definition theme pack. Copy the folder, change the pack and definition ids, and edit `spec.theme` before installing it in Settings → Presentation. Both tracked example folders are installed and compiled by `pwsh ./scripts/verify.ps1 -Scope Presentation`, so an example that drifts out of the live Presentation Pack contract fails repository verification.

A theme definition either references a built-in `spec.themeId`, or embeds a complete `spec.theme` object. Embedded themes contain `schemaVersion`, `id`, `name`, `isDark`, `colors`, `typography`, `shape`, `motion`, `background` and `morphology`. Colors use `#AARRGGBB`; preserve all semantic palette entries so controls, selection, focus and readable text remain coordinated. The v1 theme typography members remain readable for pack compatibility. The independently selected v2 Typography plan supplies display, heading, body and mono families without changing the theme palette. `spec.material.glassOpacity` and `specular` drive glass body/highlight brushes; ambient color references and rim-light values remain consumed by compatible backdrop definitions. The normal compiler validates v1 material fields, including retired fields, before installation.

`appearance.theme` is the only durable theme selection. Startup uses a fixed naut brand palette until the Vault presentation registry applies its committed theme. A catalog transaction imports a supported legacy `app.theme` or AppState theme id only when no theme binding exists, normalizes it through the built-in replacement map, removes `app.theme`, and records a receipt that survives Reset. An existing binding wins. Unsupported loose-file selections use the presentation default; custom themes are installed as packs. AppState retains its old id only until this one-time intake finishes and then omits it.

| Active theme field | Projection and consumer |
|---|---|
| Palette and light/dark | Shared `ThemeRuntime` brushes, semantic text contrast, native element theme |
| Shape `md`, `sm`, navigation radius scale | Foundation surface radius scale and Living Navigation radius; Control Set owns control radius |
| Border weight and card personality/panel opacity | `UI.Surface` borders and live Card border/body alpha |
| Navigation treatment | Living Navigation underline, accent edge, selected surface, glow tint, strong border |
| Chrome treatment | Shell navigation background and bottom border |
| Motion times/intensity | Engine duration tokens with Reduced Motion precedence |
| Glass opacity/specular | Glass surface fill and gradient highlight |
| Ambient colors/rim light | Compatible backdrop color tokens and renderer |

V1 typography and background-kind declarations remain parse-compatible but do not select fonts or Backdrops. Focus treatment, elevation strength, glow strength, material personality/atmosphere/grain/blur and frame affinity have no active theme projection. Focus semantics use the palette and Control Set. Retired duplicate color names remain read-compatible aliases; they are not exported as active tokens. Engine foundation scales, legacy radius references and accessibility metadata remain compatible export contracts. Settings and the Presentation library resolve preview colors and geometry through the same token authority as the live shell and Cards.

## Built-in Cover artwork

The showroom choices are Square, Circle, Flower, Sakura, Cyberpunk and Imperial, in that order. Settings uses three columns so the default silhouettes share the first row; Customization keeps the same order vertically. Chooser thumbnails use the PNG artwork alone or a neutral default silhouette; the active preview retains the selected Profile photo. Square, Circle and Flower clip the Cover to their named silhouette without decorative borders. The three ornaments use transparent artwork on a square stage with a circular Cover underneath; `coverInset` reserves the ornament's aperture. Cover renders first; artwork composites above it without automatic cropping. Tint and frame animation stay disabled. Retired built-in references resolve through the catalog replacement map.

Built-in source definitions live under `src/Neuterradise.App/Assets/Presentation/BuiltIn/`. `catalog.json` declares assets, definition files, slot defaults, showroom membership, and deterministic replacement references. Runtime embeds and assembles these files, then uses the same reader, validator and compiler as installed packs. Compatibility definitions, legacy theme files and v1 preset bridges remain readable for installed user packs; they do not author new showroom designs.

Frame specs may provide `coverInset` from 0 to 0.4 as a fraction of the square stage size. This reserves space for ornament around the Cover without cropping the artwork. Omission keeps the standard procedural inset used by existing user packs.

## Frame pack format

For a portable custom artwork frame, use a `cover-frame` definition with `spec.overlayAsset` referencing a declared `Image` asset. The direct frame importer produces this same manifest shape:

```json
{
  "assets": [{ "id": "frame-overlay", "kind": "Image", "path": "assets/frame.png" }],
  "definitions": [{
    "id": "myname.frame.frame",
    "kind": "cover-frame",
    "name": "My Frame",
    "spec": {
      "family": "Minimal",
      "shapes": ["RoundedSquare", "Square", "Circle"],
      "scale": 1.0,
      "tint": false,
      "animated": false,
      "category": "Ornate",
      "overlayAsset": "frame-overlay",
      "coverInset": 0.0
    }
  }]
}
```

This excerpt belongs inside the manifest contract above, with `packId` set to `myname.frame`. Include the actual transparent artwork at `assets/frame.png`. Keep a clear center opening and sufficient edge padding for the chosen Cover shape. `tint: false` preserves the artwork's colors. A built-in frame can instead be referenced with `spec.frameId`.

## Layout geometry

Use the shared density and spacing tokens as the visual baseline. Collection layouts arrange items; Card compositions own the Profile identity inside each item. Keep artwork and text within that item's bounds, permit wrapping at narrow widths, and preserve readable controls rather than applying a global scale transform. Backdrops and effects should support the foreground content and obey reduced motion and offscreen suspension policies.

Embedded theme ids use lowercase letters, digits and hyphens (up to 64 characters); the enclosing pack and definition ids also allow dots. Home hero height fractions are bounded to 0.25–0.7, minimum heights to 180–520 px and maximum heights to 280–1000 px. Profile media artwork fractions are bounded to 0.35–1 so side metadata has room in compact layouts.

Media cards expose media info and one external-open action in the upper action rail. Video/model type indicators are non-interactive text labels, separate from that rail. Profile customization opens Profile Layout by default; Card opens a dedicated editor with independent Layout and Effect choices. Profile identity forms use shared labels and equal control heights rather than mixing native header geometry. Editorial Home spotlight content has an inset inside the artwork and a measured height floor so wrapped identity text fits above pagination.

## Renderer geometry contracts

Declarative card image `stretch` owns fit policy: `uniform` preserves aspect with fit, `uniform-to-fill` preserves aspect with crop, and `fill` stretches both axes into the image slot. Profile framing still supplies focal point, zoom, offsets and rotation. These accepted values remain readable for installed packs.

Home `hero.placement=split` selects split composition in both live and preview rendering and takes precedence over Spotlight composition. Other placements preserve the selected Spotlight composition.

Media `metadataLines` accepts 0–3. Nonempty name, detail and favorite groups occupy those rows; the last available row merges remaining groups. `added` uses the existing catalog addition timestamp, displayed in local culture; an absent timestamp is omitted. Zero rows intentionally hides metadata. Customization miniatures scale the complete renderer, including typography, within their finite stage; Profile framing preview scrolls the shared header at its natural height.

## Surface composition and Gallery items

Home and Profile definitions may add a bounded `spec.surface` tree of stack, columns, panel, overlay, spacer, divider and semantic nodes. Trees have at most 48 nodes and depth 8. Semantic regions consume engine data and commands; they cannot execute queries or scripts. Home requires featured, activity and statistics. Profile requires identity, overview, media, related, faces, actions and review. `recent-media` is an optional Profile semantic region. When present, it remains subject to the Profile-owned Recent media visibility preference; a Presentation Pack cannot force it visible, and selecting another built-in or user layout does not reset that preference. Enabling the preference does not synthesize `recent-media` into a layout that did not author it. Responsive columns collapse below 800 pixels.

Gallery definitions own only the collection container, envelope and layout, including toolbar treatment (`feed`, `catalog`, `drawer`, or v1 `standard`). Every Gallery item resolves its Profile-owned `profile.card`; a Gallery pack cannot override Card composition. The retired `itemDefinition` property is ignored when reading existing packs. `heightPattern` contains one to eight item aspects between 0.5 and 2.5; its virtualized masonry host realizes only the viewport and buffer. Media `browse` selects wall, workbench or horizontal reel; selection, paging, favorite, hover and Inspector/EXIF actions stay engine-owned.

## Typography and managed asset intake

Typography declares `display`, `heading`, `body` and `mono` faces, each with a plain `family` and optional declared Font `asset`. Local font files load through Uno's font fallback service. Content-derived aliases prevent replaced user fonts from colliding with cached typefaces. Clear, Instrument and Literary ship static Latin faces plus local Japanese, Korean and Simplified Chinese fallback; no network font loading is used. Font provenance and OFL licenses ship beside the files. Modified IBM faces use the Naut Instrument names.

Settings → Presentation accepts local `.ttf`/`.otf` files and backdrop images or MP4s. Intake validates and installs a one-definition pack; durable bindings refer to managed pack assets rather than picker paths. Custom fonts and backdrops use the existing replacement/export/removal authority.

Backdrop is a quiet background decision, separate from Theme, Effects and Banner. Ordinary Home/Profile choices are **None**, **Calm Yellow**, **Calm Blue**, and **Calm Gray**; both surfaces default to None. The calm tones use a shared static canvas-relative tint policy so each remains visibly distinct across light and dark themes. Runtime and previews use the same renderer. Animated pack previews share the visual scheduler; additional previews retain authored static variants, or a compiler-derived static environment when their media variants contain no static layer. Retired living/media backdrops normalize to Calm Gray, while Still normalizes to None. Direct backdrop intake lives in Settings → Presentation; it is not an ordinary Home/Profile Customize action. Installed pack capability remains available through the Presentation library.

## Control skins and effects

The durable `appearance.control-skin` slot resolves a Control Set. V1 root radius, border width, padding, height, scrollbar size and state colors remain the fallback for every family. Optional `spec.families` objects override those values for `button`, `icon-button`, `chip`, `tab`, `input`, `toggle`, `slider` and `scrollbar`. Shared factories, native-control resources and weak registrations apply the same plan and refresh live controls. Primary accent, danger, ghost transparency and checked selection remain semantic engine colors. Commands, keyboard/pointer behavior, accessibility and selection authority remain engine-owned; signature Living Navigation remains its own component. Settings and the library display an actual miniature control sheet.

An Effect declares compatible engine target ids or `all`, one of environment/particles/lighting/surface/interaction channels, and full/reduced/fallback variants. Channels remain pack/compiler metadata. Ordinary built-in runtime has one layer per Home (`effect.home`, surface-scoped), Profile (`effect.profile`, Profile-scoped), and Card (`effect.card`, Profile-scoped), with None defaults and curated choices **None**, **Bubbles**, **Rain**, **Snow**, and **Silk**. Compatible user effects must support the selected target. Effects never cascade. The extended slot registry remains for pack compatibility, but built-in rendering does not attach Controls, Banner, Cover, Frame, Media, or Backdrop child effect layers. Effects render above the background/art base and below all foreground content without intercepting input.

Profile Customize is a right inspector over the live surface. Its Media launchers (Cover, Banner, Frame and Media) are grouped first, followed by the engine-owned 3D Figure control; Layout, Backdrop and Profile Effect follow as inline choices. The Layout group also owns the Profile-wide Recent media visibility toggle; it is not duplicated per layout definition. Cover, Banner, Frame, Media and Figure open bounded floating forms sharing the same PreviewSession. Floating Save publishes that shared session and closes Customize after success, without another inspector Save. Other floating forms use Back to return while preserving staged state; Figure Back restores only the Figure choice present at chooser entry. Clicking a Figure model directly selects it in the draft, with no additional confirmation button. Closing or clicking outside the inspector closes immediately when clean and asks before discarding when dirty. Profile editing waits for the authoritative subject snapshot before exposing choices. Card's own signature Customize Card action opens an independent floating form for profile.card and effect.card with one real Card preview. Card customization is absent from the Profile inspector.

Live and preview identity geometry use finite allocation independent of the native Figure child's desired size. Layout switching and modal occlusion retain the existing viewport/camera while hiding or suspending it as needed; pack geometry cannot turn native hosting into an unbounded measure/arrange loop. Figure framing, pedestal, orbit/pan/zoom and texture-quality policy remain engine-owned and do not add pack parameters.

Effects contain only approved engine primitives, with at most three layers, eighty particles, and combined layer opacity 0.6. Interaction-only definitions require pointer or focus activity. Weather, particles, fog, lighting, flow, pattern, grain, celebration and component decorations share bounded primitives; user shaders/scripts and media sources are prohibited. Non-None effects retain visual continuity at Reduced and static Fallback tiers. The compiler derives missing or empty effect tiers from Full with fewer particles and lower speed, then freezes the static representation. At startup Naut prepares the five curated built-in effects across full/reduced/fallback variants using bounded off-screen procedural draws. Prewarm only primes renderer/JIT/resource paths; it is never a delayed admission gate. Settings Effect preview uses the same renderer as runtime. Loaded visible hosts share a scheduler ordered by surface, active Card, visible Card, selected preview, visible preview and Settings preview. Six hosts receive Full quality; additional visible runtime hosts retain Reduced effects. At most three preview hosts animate, with remaining previews rendering static fallback. Hidden/offscreen/unloaded hosts and suspended warm surfaces stop. Clocks are capped at 30 FPS (15 at Reduced tier). The None state creates no backdrop layer subscriptions. OS Reduced Motion uses the static fallback.

Pack diagnostics are available in **Settings → Presentation → Packs / Advanced** and Vault Health. Everyday Presentation library views intentionally show asset identity and provenance rather than raw pack metadata. The headless probe `pwsh ./scripts/verify.ps1 -Scope Presentation` validates catalog/defaults/assets, compatibility, independent selection, preview transactions, restart, repository examples, legacy Theme migration, tier continuity, visual-quality policy, calm tint separation and managed pack lifecycle in a disposable draft-Profile Vault. Use `-Offline` for restore from the local package cache without network sources. Pass `-BuiltInAssetsRoot dist/naut/runtime/Assets/Presentation/BuiltIn` to exercise packaged assets with the current source runtime. `pwsh ./scripts/verify.ps1 -Scope Theme` checks active token consumers and retired selection boundaries; compile and build invoke that internal contract. These checks provide no interaction or playback acceptance.

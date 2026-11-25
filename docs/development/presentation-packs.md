# Presentation Packs

Presentation customization has one transport and validation authority: a **Presentation Pack** with a `pack.json` manifest. Do not add a second standalone-theme/layout/frame loader. A pack may contain **one definition only**, so a single custom component stays lightweight while using the same validation, persistence, replacement, export, and removal path as larger packs.

Presentation Contract 4 keeps manifest and binding schemas at version 1 and reads Contract 1, 2 and 3 packs. **Settings -> Appearance** is the single global selector for Theme, Font and Control Style, including compatible installed-pack definitions. **Settings -> Presentation** is the component library: it lists built-in and installed Fonts, Frames, Backdrops and Effects for preview and direct-file management, not for selection or application. Its guidance directs users to Appearance for global choices and to the relevant Home/Profile Customize panel for scoped components. Presentation opens on Packs / Advanced for pack installation, export, removal and diagnostics. The Fonts, Frames and Backdrops tabs retain direct imports. Individually imported files are internally stored through the same pack authority, but carry a local intake marker that is not exported. Their Settings library cards show a direct Delete action, which uses the existing atomic pack removal and resets any bindings that use the file. Files installed as `.ntpack`, ZIP or pack folders stay contract-managed in Packs / Advanced and never receive an individual Delete action; built-in components are read-only. Older installations without the intake marker remain conservatively pack-managed because their import source cannot be proven. Library cards show previews and Default/Built-in/User provenance with an individual-file/pack-managed indicator, not selection controls or raw pack metadata. Icon semantics, commands, selection, focus, queries, media readiness, accessibility, and ResourceGovernor admission remain engine-owned.

The built-in customization UX exposes Home Effect, Profile Effect and independent Card Effect decisions. Gallery has no surface effect picker and Settings has no Effect picker. Primary built-in choices are None, Fireflies, Rain, Snow, Bubbles, Light Sweep and Wave, plus compatible definitions installed by the user. Former built-in Effect definitions are removed; historical selection IDs normalize to one of the curated Effects or None. User-installed pack Effects retain the shared engine primitives. Profile Effect and Card Effect are independently Profile-scoped; Home Effect is surface-scoped. No target inherits another target's effect. Customize Profile → Banner offers None, Film, Glitter, Shimmer, and compatible installed effects, with per-Profile intensity defaulting to 20%. These choices persist with Profile appearance and render through `effect.profile.banner`, including Home Spotlight. Grain uses a bounded reusable texture; glitter and shimmer use the existing particle and light-sweep primitives. The detailed engine slots remain intact for Presentation Pack compatibility. **Save changes** commits the existing preview transaction; Cancel asks before discarding staged changes.

## Select from pack

Open **Customize Home** or **Customize Profile** and select **Select from pack** in the inspector. The searchable list shows installed packs with at least one compatible definition for the current editor. Home fills Home Layout, Spotlight, Backdrop and Effect. Profile fills Profile Layout, Media Layout, Backdrop, Profile Effect, Frame, Banner Effect, Card Layout and Card Effect for the Profile being edited. Home Spotlight and Gallery cards continue to render Profile-owned choices through their existing Profile resolution.

Naut selects the first compatible definition in manifest order for each slot and keeps existing selections for omitted slots. You can select another pack or replace any individual choice before **Save Changes** commits the preview. Cancel discards the preview. Gallery Layout and the global Appearance controls have their own pickers.

## Minimal component workflow

1. Extract `docs/examples/presentation-pack-contract4.ntpack` (a ZIP-compatible archive) into a working folder, or install it directly from Packs / Advanced.
2. Give `packId` a stable lowercase id such as `myname.gallery-layout`.
3. Every definition id must begin with that pack id plus a dot, for example `myname.gallery-layout.grid`.
4. Keep only the definition(s) you need. `assets` may be empty.
5. In **Settings → Presentation → Packs / Advanced**, choose **Install from folder…** and select the folder containing `pack.json`.
6. Choose an individual definition from its normal customization surface, or use **Select from pack** in Home or Profile and save the preview.
7. To update the same pack id, install it again and approve **Replace**. The replacement is validated before becoming authoritative.
8. Installed user packs can be **Exported** to a portable `.ntpack` and **Removed** from the same screen.

Fonts accept `.ttf`/`.otf`; Backdrops accept `.png`, `.jpg`, `.jpeg`, `.webp` or `.mp4`; direct Frames accept square transparent `.png`/`.webp` artwork from 256 to 4096 px. Effects and Control Styles can be added as supported declarative pack definitions.

A `.zip` or `.ntpack` containing `pack.json` is also accepted. An archive may wrap the pack in one top-level directory.

## Direct custom frame workflow

**Settings → Presentation → Frames → Add custom frame…** accepts a square transparent `.png` or `.webp` between 256×256 and 4096×4096 pixels. The center of the artwork must be transparent so the Profile Cover remains visible. Direct custom frames use the same square stage for the Cover and frame artwork: naut renders the Cover first at the full frame bounds, then composites the transparent frame PNG above it, so there is no artificial gap between the Cover and decorative artwork.

This direct intake is an authoring shortcut, not a second frame authority. naut validates the artwork, builds a one-definition user Presentation Pack in temporary staging, installs it through the normal pack validator/compiler/store, and makes that `cover-frame` definition available in Profile Customize. Installation does not select it for a Profile; selection uses the normal preview transaction and **Save changes** action. Installed custom frames remain pack-backed internally but show Delete directly under **Settings → Presentation → Frames** when their individual-file intake origin is recorded. An exported `.ntpack` reinstalled later is a contract-managed pack and is removed from **Packs / Advanced**.

Direct image intake is intentionally static. More advanced custom frame behavior remains declarative Presentation Pack territory so motion/effects continue to pass through the same bounded renderer and performance authority instead of introducing executable or arbitrary shader content.

## Manifest contract

The current pack schema is `1` and Naut implements Presentation Contract `4`. Set `minContractVersion` to the lowest contract the definitions require. A minimal manifest contains:

- `schemaVersion`, `packId`, `version`, `name`, and `minContractVersion`;
- optional `author`, `description`, and `license`;
- `assets`, which may be an empty array;
- `definitions`, with one or more declarative definitions.

Pack and definition ids use lowercase letters, digits, `.`, and `-`. A definition id must start with `<packId>.`.

Supported definition kinds are:

`theme`, `home-layout`, `backdrop`, `spotlight-style`, `gallery-layout`, `profile-card`, `profile-layout`, `cover-frame`, `profile-media-layout`, `typography`, `control-skin`, `effect`.

The `spec` object is kind-specific and is compiled by the existing Presentation compiler. Use a built-in definition of the same kind as the reference shape; do not bypass the compiler by adding ad-hoc runtime properties. A `gallery-layout` is only the virtualized Gallery shell: its collection primitive, item envelope, spacing and column/row limits control arrangement. It never selects or replaces `profile-card`; each Profile keeps its own card definition inside that shell. Gallery cards always receive the same Profile identity data (name, category, tags, favorite state, and rating); the Profile Card composition decides where those values appear.

## Assets and safety

Assets are optional for definitions other than Theme. Every Theme requires a representative static image declared with kind Image under pack assets and referenced by `spec.artwork`. The image is a visual identity swatch/illustration, not a miniature screen or live Theme preview. This requirement applies to built-in and installed Theme definitions. Assets are declared by id, kind, and relative path and resolved only through the pack asset store. Installation stages the selected folder/archive, validates containment, manifest fields, assets, definition kinds, and compiler semantics, then moves the validated pack into the user presentation-pack authority.

Profile-card `hover` accepts `lift`, `sheen`, `spotlight`, `banner` or `none`. `banner` preserves the Cover composition at rest and hosts the selected ready Banner hover video inside its artwork area, as used by Editorial. Installed user packs appear after the built-in library in Packs / Advanced, and their supported components appear in the corresponding library tabs.

`hover: banner` requires exactly one Banner image or one non-ambient Cover image in the composition. Frame-only, text-only, ambient-Cover-only, and multiple-host compositions are rejected. Imported Backdrops are offered in Home and Profile Customize according to their `home-only` or `profile-only` tags; omission makes them available to both targets.

Presentation packs are data/art only. Executables, scripts, source files, shaders, links, SQL, XAML, and other forbidden content are rejected. Pack installation is separate from media import and does not turn presentation assets into Vault media.

Manifests are limited to 4 MiB and malformed field types produce validation diagnostics. Folder intake checks the selected root and traversed directories for links or reparse points. Images must decode completely within a 64 MiB pixel budget. Video installation requires approved packaged tools to inspect a video stream and decode its first frame with bounded execution; header bytes alone cannot establish a usable video. Unavailable tools reject video intake with a diagnostic.

Replacement preserves the prior generation until catalog publication succeeds. Install and removal record an operation journal before moving a directory; startup reconciles interrupted operations against the catalog's recorded content hash before loading packs. Recovery verifies generation hashes and containment before cleanup and preserves changed or ambiguous bytes. Committed cleanup records each file before deletion so a locked file or interrupted partial cleanup can be retried without blocking the active pack. A remaining operation blocks another change to that pack until recovery settles it. These operation receipts are recovery metadata, not a second selection authority.

Pack definitions consume the selected Profile Cover and Banner MediaAssets through their composition rules. A layout or theme change does not compile, trim, or transcode media. Fit, crop, and focal placement are rendering choices.

The Profile Figure is an engine-owned use of canonical Model Media and its prepared ModelRender derivative. It is part of the shared identity header, not a Presentation Pack definition kind, slot or body module. Profile layouts may position the identity semantic region, but cannot force a Figure source, replace its renderer, introduce shaders or bypass its exclusive native-viewport/resource policy. Home, Gallery and media cards retain static model thumbnails.

## Repository example

`docs/examples/presentation-pack-contract4.ntpack` is the single complete authoring and ready-to-install example: one definition per supported kind, four small local assets, and Contract 4 scene, navigation, Gallery, multi-banner and skin declarations. Install it directly or extract it to edit `pack.json` and assets. Keep only the components and referenced assets needed for a smaller pack.

## Custom theme format

The complete example includes an embedded custom theme. Extract the archive, change the pack and definition IDs, and edit `spec.theme`. Repository Presentation verification installs, compiles, exports and reinstalls the example so drift from the live contract fails verification.

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

Built-in source definitions live under `src/Neuterradise.App/Assets/Presentation/BuiltIn/`. `catalog.json` declares assets, definition files, slot defaults, showroom membership, and deterministic replacement references. Runtime embeds and assembles these files, then uses the same reader, validator and compiler as installed packs. Only canonical curated built-in definitions are shipped. Historical built-in IDs are normalized through a direct migration map to these definitions; installed user-pack definitions remain independent and are not modified.

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

Media cards expose media info and one external-open action in the upper action rail. Favorite Media uses a neutral stroke Flame when inactive and a warm orange (`#FF9A45`) stroke Flame when active, shared with the Media Inspector; this does not affect the filled rating star or Profile favorite heart. Video/model type indicators are non-interactive text labels, separate from that rail. Profile customization opens Profile Layout by default; Card opens a dedicated editor with independent Layout and Effect choices. Profile identity forms use shared labels and equal control heights rather than mixing native header geometry. Cinematic Home Spotlight uses a bounded scrim and compact metadata over artwork. Focus centers a Cover/frame and Profile metadata directly over the full-bleed banner. Editorial uses the same full-banner media with a translucent lower information layer instead. All three keep pagination and identity responsive without an opaque title card; reduced motion and media fallback remain engine-owned.

## Renderer geometry contracts

Declarative card image `stretch` owns fit policy: `uniform` preserves aspect with fit, `uniform-to-fill` preserves aspect with crop, and `fill` stretches both axes into the image slot. Profile framing still supplies focal point, zoom, offsets and rotation. These accepted values remain readable for installed packs.

Custom Spotlight compositions can use Home `hero.placement=split` for wide split artwork and identity; the curated Focus, Editorial, and Cinematic Spotlights instead use full-bleed artwork with center, translucent lower, and minimal-copy identity placements. Showcase and Journey retain full artwork/inset arrangements. Spotlight independently controls copy, cover, motion, scrim intensity, rotation and pagination, never forcing a split or an opaque rectangular title box. The lower fade is bounded by artwork geometry, not the page, and changes to Home width recompute typography and navigation allocation. Live Home and miniatures use the same layout/Spotlight contract.

Media `metadataLines` accepts 0–3. Nonempty name, detail and favorite groups occupy those rows; the last available row merges remaining groups. `added` uses the existing catalog addition timestamp, displayed in local culture; an absent timestamp is omitted. Zero rows intentionally hides metadata. Customization miniatures scale the complete renderer, including typography, within their finite stage; Profile framing preview scrolls the shared header at its natural height.

## Surface composition and Gallery items

Home and Profile definitions may add a bounded `spec.surface` tree of stack, columns, panel, overlay, spacer, divider and semantic nodes. Trees have at most 48 nodes and depth 8. Semantic regions consume engine data and commands; they cannot execute queries or scripts. Contract 1 Home requires featured, activity and statistics. Contract 1 Profile requires identity, overview, media, related, faces, actions and review. `recent-media` is an optional Profile semantic region. When present, it remains subject to the Profile-owned Recent media visibility preference; a Presentation Pack cannot force it visible, and selecting another built-in or user layout does not reset that preference. Enabling the preference does not synthesize `recent-media` into a layout that did not author it. Portrait and Dossier each author a `recent-media` region in their custom surface tree. Portrait and Dossier place Related immediately after Overview, with Face Review kept separately at the foot of the module content. The app owns a single Delete Profile footer strictly below every module, for built-in and optional pack layouts alike. The `actions` semantic remains required for Presentation Contract v1 compatibility but is omitted from the rendered body (including its empty container/column), so packs cannot move or duplicate the destructive button. Chronicle preserves the required semantic surface declarations while displaying Overview and Related inside the shared identity header, horizontally centered on the Cover-and-name information column beside the Figure rather than across the entire Profile. The panels are not repeated in the content body. Its Recent media module remains subject to the same Profile-owned visibility preference. Installed layouts may omit `recent-media`. Responsive columns collapse below 800 pixels.

### Modular surfaces and artwork

Keep `schemaVersion: 1` and declare `minContractVersion: 2` for the capabilities below. A `spec.surface` using `"modular": true` still requires Home's `featured` or Profile's `identity` and `media`. Other semantic regions are optional. Shell navigation remains available when Home omits `local-navigation`. Home's **Show Recently** preference defaults to on and gates `activity` independently of `recent` (Continue). It is saved with the Home layout binding and resets to on with the Home layout.

A `grid` declares `rows` and `columns` using auto, star, or fixed tracks. Children use zero-based `row` and `column`, with `rowSpan` and `columnSpan` defaulting to one. Siblings can share a cell or use `overlay`; later children paint above earlier ones. Tracks without a supplied child or span collapse. Put an alternate root at `surface.narrow` and its threshold in DIP at `surface.breakpoint` (default 800). Each arrangement may position the same semantic region once. The renderer selects the arrangement using available content width.

This `profile-layout` spec places a single Figure next to Banner on a wide viewport and below Banner on a narrow viewport. A selected Figure has a fallback position below the layout when an arrangement omits it.

```json
{
  "preset": "portrait",
  "surface": {
    "modular": true, "type": "grid", "breakpoint": 860,
    "rows": ["auto", "auto", "auto"], "columns": ["2*", "*"],
    "children": [
      { "type": "semantic", "id": "banner" },
      { "type": "semantic", "id": "figure", "column": 1 },
      { "type": "semantic", "id": "identity", "row": 1, "columnSpan": 2 },
      { "type": "semantic", "id": "media", "row": 2, "columnSpan": 2 }
    ],
    "narrow": {
      "type": "stack",
      "children": [
        { "type": "semantic", "id": "banner" },
        { "type": "semantic", "id": "figure" },
        { "type": "semantic", "id": "identity" },
        { "type": "semantic", "id": "media" }
      ]
    }
  }
}
```

This `spotlight-style` spec places identity and controls below a disk. Declare `disk-mask` and `disk-rim` as Image assets pointing to local PNG files in the same pack. The mask uses alpha, including transparent holes and partially transparent edges. Export both assets on matching square canvases. Declare `continuousAnimation: true` and `maxAnimatedLayers: 1` in the definition's performance object.

```json
{
  "motion": "still",
  "identityPlacement": "below",
  "artwork": {
    "aspect": 1,
    "mask": "disk-mask",
    "layers": [
      { "type": "artwork" },
      { "type": "image", "asset": "disk-rim",
        "rotation": { "direction": "clockwise", "seconds": 18, "pivotX": 0.5, "pivotY": 0.5 } }
    ]
  }
}
```

The artwork layer plays the normal Spotlight video or shows the fallback image. Rotate only the rim to keep the video upright. To turn the mask, rim and video together, move the rotation object from the rim to the artwork group. `scrim` and `effect` can also appear in the ordered group. The same group syntax is available under `spec.bannerArtwork` for a Profile layout. Banner framing and Figure controls remain outside the visual group. Rotations pause outside Full quality and with Reduced Motion enabled. Artwork without authored rotation does not subscribe to motion changes. Mask composition resources are attached only after a successful image load; unloading retires the current mask generation, and late completions release their own image surface without reattaching it.

### Contract 4 Gallery scenes

A gallery-layout may declare `spec.scene` with a `background` and/or `foreground` using the existing backdrop variant format, and an optional `itemOverlay` declared image asset. The environment is rendered once per Gallery viewport, not per Card; the overlay is a decorative, non-interactive layer on each realized item. `itemInset` reserves a symmetric fraction (0–0.3) of its envelope for authored framing. `cardSizing: "contain"` preserves each Card's own aspect inside the layout envelope. `"intrinsic"` uses virtualized masonry with heights derived from the respective Card aspect, for vertical grid and shelf collections. `cardSizing: "crop"` explicitly clips the Card within its own item envelope; the default never crops. `cardRotation` and `cardSkew` accept -20 to 20 degrees and apply bounded affine geometry. `overlayPlacement` chooses `front` or `behind`; overlap stays inside the item envelope, preserving virtualization and native hit targets. The Gallery's native toolbar, selection, pagination and item action remain native. Animated environments use the existing backdrop scheduler and stop on surface suspension.

The dedicated Cover and Frame editors show the actual Cover with its chosen Frame, using the existing Cover renderer. The dedicated Banner editor renders the current Banner artwork, mask and effects without Profile identity or Figure. The sidebar and main-page preview remain available; while a dedicated modal editor is open, the occluded Profile header pauses playback to avoid duplicate decoding.

### Contract 4 scene graphs and skins

Declare `minContractVersion: 4` and `spec.scene` instead of `spec.surface`. Both fields compile into the same surface plan; declaring both is invalid. Existing stack, grid, columns and semantic layouts retain their behavior. A modular scene may contain a `canvas` with a finite aspect ratio (0.25–6) and explicitly positioned children. Coordinates use the artwork placement convention: normalized x/y offsets, width/height fractions, horizontal/vertical anchors and order. Image and shape leaves are decorative and do not intercept native input. The existing narrow arrangement and allocated-width breakpoint also apply to scenes. Profile media, faces and recent media remain in the native scrolling collection outside the canvas. Node/depth budgets and required semantic regions still apply.

```json
{
  "scene": {
    "modular": true,
    "type": "canvas",
    "aspect": 2,
    "breakpoint": 500,
    "children": [
      { "type": "shape", "fill": "token:canvas", "position": { "order": 0 } },
      { "type": "semantic", "id": "featured", "position": {
        "x": 0.1, "width": 0.8, "height": 0.8, "order": 1
      }}
    ],
    "narrow": { "type": "stack", "children": [
      { "type": "semantic", "id": "featured" }
    ]}
  }
}
```

Decorative image and shape nodes may declare `motion`: a bounded timeline with `seconds` (0.25–120), `loop`, and 2–32 `keyframes` with strictly increasing normalized `time` from 0 to 1. Each frame describes `x` and `y` offsets relative to its own bounds, uniform `scale`, `rotation` in degrees and an `opacity` multiplier. Declare `maxAnimatedLayers` and, for loops, `continuousAnimation: true` in definition performance. Motion does not change the layout or native interaction geometry. One shared render clock drives backdrop and scene ticks; at most eight scene layers animate globally at up to 30 updates per second. Inactive, offscreen, reduced-motion, scrolling and memory-pressure states restore the static artwork; unloading removes its clock registration. A non-looping timeline holds its final frame while active.

A `typography` definition may add `spec.styles` keyed by existing semantic type roles (such as `hero` or `control`), with `font`, `size`, `weight`, `lineHeight` and `color` overrides. Font assets and multilingual fallback use the existing registration authority. A `control-skin` definition may add `spec.icons` keyed by native semantic icon names, with `path`, optional `selectedPath` and `strokeWidth`. Vector paths must be nonempty, parseable, bounded to the 24×24 design box and at most 256 path points. Native control state selects the alternate geometry without changing commands. The chip family supports `passiveHeight` (12–96 DIP); the icon-button family supports `iconButtonSize` (24–96 DIP). Contract 4 accepts control radii up to 64 and heights up to 96 DIP. Omitted properties retain existing defaults. Contract 4 control skins may also declare explicit `chrome`, `navigation` and `breadcrumb` families for the top-bar and breadcrumb surfaces. Native route targets and controls remain unchanged, and chrome height accommodates the configured control geometry. Navigation accepts a `selectedFill` override; Customize retains its signature semantics. `spec.navigation` on a control skin may declare a `scene`, `dock` (`top`, `bottom`, `left`, `right`), `extent` (48–240 DIP), and `immersive`. Every arrangement must include the native `vault`, `home`, `gallery`, `import`, `settings`, `activity` and `customize` regions. Native navigation remains available through F10 and the recovery control; Escape reveals or hides immersive navigation outside dialogs and editors. Narrow windows and protected routes use the native layout.

A Contract 4 Profile media layout may declare `collection` with `layout` (grid or editorial), `maxColumns` (1–12), `rowSpacing` and `columnSpacing` (0–48 DIP). In this mode the authored `cardWidth` is used up to the available width instead of the legacy mode cap. Collections retain the existing ItemsRepeater and paged/virtualized media authority. Omission preserves the legacy renderer behavior.

Artwork in Spotlight and Profile Banner may add ordered `{ "type": "banner", "sourceIndex": 1, "placement": { ... } }` layers alongside the required primary `artwork` layer. Indices refer to distinct ready entries from the Profile's ordered Banner collection, excluding the primary selection; the first additional entry is index 1. Resolution is on demand only. Video leases share a decoder and decoded frame when path, loop and still mode match. Across Banner, Backdrop, preview and hover, Full admits up to two distinct active decoders, Reduced up to one, and Suspended or memory pressure admits none. Primary Banner has priority; non-admitted instances retain poster fallback. Reduced Motion, scrolling, memory pressure, offscreen artwork and inactive scenes prevent secondary playback. A released secondary decoder slot is offered to the next eligible scene layer. Missing sources remain transparent. Existing packs use the same renderer and no additional Banner resolution unless they author these layers. Each secondary layer may declare a stable `slotId` (default `banner-{sourceIndex}`); the native Customize Banner editor exposes the primary Banner and the authored secondary slots from the selected Profile and Home Spotlight definitions. Secondary slots save source Media ID, focal point, zoom, rotation, effect reference and effect intensity in the Profile's appearance schema 1. Automatic source uses the ordered collection; explicit selection is checked against the Profile's ready Banner membership before Save. Cancel leaves the committed appearance unchanged. The primary Banner retains its existing source selection and framing and has an independent effect and intensity, stored in the reserved `primary` appearance slot. The existing Banner Effect binding controls the scene-wide effect. Secondary preview isolates the selected source and its own effect; scene-wide mask and overlay remain on the main page. A secondary instance selecting the primary source shares its current frame with independent framing, rotation and effect. Static image decoding, including WebP and GIF, requests a single frame; continuous video decoding uses the shared video authority.

### Spotlight regions and responsive artwork placement

Use `minContractVersion: 3` for an independent Spotlight `spec.surface`, layer placement, or explicit module sizes. On older Naut versions the pack manager reports the required contract version.

In a `spotlight-style` spec, `surface` accepts the modular grid, stack, and overlay syntax. Each arrangement supplies `artwork` and `identity` once. Add `pagination` separately when desired. Its buttons and keyboard actions come from Naut. This example uses a transparent asymmetric mask and a decorative PNG that turns independently of the video. Declare `cutout` and `edge` as Image assets, and set the definition's animation budget to one.

```json
{
  "motion": "still",
  "surface": {
    "type": "grid", "breakpoint": 760,
    "rows": ["auto", "auto"], "columns": ["3*", "2*"],
    "children": [
      { "type": "semantic", "id": "artwork", "height": 340, "rowSpan": 2 },
      { "type": "semantic", "id": "identity", "column": 1 },
      { "type": "semantic", "id": "pagination", "row": 1, "column": 1 }
    ],
    "narrow": { "type": "stack", "children": [
      { "type": "semantic", "id": "artwork", "height": 230 },
      { "type": "semantic", "id": "identity" },
      { "type": "semantic", "id": "pagination" }
    ] }
  },
  "artwork": {
    "mask": "cutout", "breakpoint": 440,
    "layers": [
      { "type": "artwork" },
      { "type": "image", "asset": "edge",
        "placement": { "width": 0.66, "height": 1, "horizontal": "right", "order": 1 },
        "narrow": { "width": 0.82, "height": 0.95, "horizontal": "center", "order": 1 },
        "rotation": { "seconds": 20, "direction": "clockwise" }
      }
    ]
  }
}
```

Layer `placement` and optional `narrow` set normalized width, height, x and y offsets, horizontal and vertical anchors, and paint order. Offsets are relative to the artwork bounds. Omitted values fill the artwork bounds with no offset, and order defaults to manifest order. Use the same placement syntax inside Profile `bannerArtwork`.

For a modular `profile-layout`, explicit dimensions use DIP. In the wide arrangement from the Profile example, set the independent Figure region to `{ "type": "semantic", "id": "figure", "column": 1, "width": 460, "height": 360 }`. In the narrow stack, use `{ "type": "semantic", "id": "figure", "height": 240 }`. A width caps the region's allotted width. When height is omitted, Figure uses its default standalone size.

Gallery definitions own only the collection container, envelope and layout, including toolbar treatment (`feed`, `catalog`, `drawer`, or v1 `standard`). Every Gallery item resolves its Profile-owned `profile.card`; a Gallery pack cannot override Card composition. The retired `itemDefinition` property is ignored when reading existing packs. `heightPattern` remains a one-to-eight-ratio compatibility field (each between 0.5 and 2.5) that enables virtualized masonry; measured height follows the actual Profile Card aspect plus its action row. Only viewport items and a buffer are realized. Media `browse` selects `wall`, `editorial` or `workbench`; legacy `reel` input resolves to wall. The Profile uses one page scroll and 24/48/96-item pagination. Selection, paging, favorite, hover and Info/People actions remain engine-owned. The current built-in layouts are Mosaic, Editorial (`editorial-grid`) and Workbench. Workbench fits three compact columns into a wide Profile Media region, with image previews and side metadata. Mosaic remains a dense square image wall with hover metadata; Editorial aligns framed media rows with contiguous caption panels for filename, file format and video duration. Gallery Pinboard packs Profile Cards at their true displayed aspect without gaps after Customize Card. Catalogue uses up to two columns and adds four bordered information cells beside the Profile Card: persisted Overview (or empty-state text), related-Profile count, and actual creation/update dates. Bare historical `editorial` and `compact` appearance values continue to map to Workbench; historical built-in definition references retain their catalog replacement mappings. Retired Reel references resolve to Mosaic. Built-in Home layouts use Recent as the collection rail; installed layouts may still request Favorites.

## Theme artwork

Every `theme` definition must declare `spec.artwork`, referencing the id of an `Image` asset in **the same pack**. This is the required representative thumbnail in Settings → Appearance. An ordinary preview image (`preview.png`), remote URL, path outside the pack, missing reference, or an asset of the wrong kind is never substituted; validation rejects Themes without a valid `spec.artwork`. The Theme image is a compact illustration of the authored palette and material identity, **not** a scaled application screen, live preview, or collection of miniature controls. This requirement applies when installing or replacing a Theme pack; independently installed non-Theme packs do not need Theme artwork. The manifest schema remains at 1. Existing Theme definitions can continue to declare Presentation Contract 1. Update older Theme packs to include a declared thumbnail before importing them into this development build. Theme selection remains a single-click committed binding and must never modify another customization slot.

The manifest's `assets` array includes the following Image entry:

```json
{ "id": "theme-icon", "kind": "Image", "path": "assets/theme.webp" }
```

The Theme definition must include `"artwork": "theme-icon"` inside `spec`, alongside its existing complete `theme` or `themeId` declaration. This asset references the actual local file in that pack; no separate preview manifest or fallback image field is introduced.

Both Settings Theme views use the same static artwork-only component, with an artwork-first viewport and separate readable captions. There is no live UI miniature, synthetic seal, or mini controls. Images use the existing bounded derived-image cache at a 512-pixel decode width, load only while mounted, and release leases on unload. User-pack content hashes identify artwork generations, so replacement cannot reuse the old decoded identity. Pack replacement/removal invalidates compiled plans and refreshes both library views through the ordinary registry event.

Obsidian (charcoal/crimson), Logbook (ivory/ink/olive) and Foundry (graphite/copper) form the primary Theme set. The obsolete Night Sect, Iron Harbor, Greenroom and related built-in definitions and promotional artwork are removed. Their historic IDs resolve to Obsidian, Logbook or Foundry through one-way migration. All three canonical Themes ship their own small representative illustrations and use semantic canvas, surface, control and text colors independently of those illustrations. Theme selection never selects Typography, Control Style, Backdrop, Effects, Layout or Frame.

## Typography and managed asset intake

Typography declares `display`, `heading`, `body` and `mono` faces, each with a plain `family` and optional declared Font `asset`. Local font files load through Uno's font fallback service. Content-derived aliases prevent replaced user fonts from colliding with cached typefaces. Clear, Instrument and Literary ship static Latin faces plus local Japanese, Korean and Simplified Chinese fallback; no network font loading is used. Font provenance and OFL licenses ship beside the files. Modified IBM faces use the Naut Instrument names.

Settings → Presentation accepts local `.ttf`/`.otf` files and backdrop images or MP4s. Intake validates and installs a one-definition pack; durable bindings refer to managed pack assets rather than picker paths. Custom fonts and backdrops use the existing replacement/export/removal authority.

Backdrop is a quiet background decision, separate from Theme, Effects and Banner. Primary Home/Profile Backdrops are **Night Chamber**, **Archive Paper**, and **Tidal Glass**; **None** remains the disabled choice. Fresh selections default to Night Chamber. Historical Calm Yellow/Blue/Gray and retired media Backdrop IDs now migrate directly to one of the three redesigned Backdrops or None, without loading an obsolete renderer. Each new Backdrop uses distinct authored layers and the same active visual scheduler. Runtime and previews use the same renderer. Animated pack previews share the visual scheduler; additional previews retain authored static variants, or a compiler-derived static environment when their media variants contain no static layer. The archived Still selection normalizes to None. Direct backdrop intake lives in Settings → Presentation; it is not an ordinary Home/Profile Customize action. Installed pack capability remains available through the Presentation library.

## Control skins and effects

Built-in typography uses a shared 9-12 DIP scale: body 10, metadata and badges 9, Card and section titles 11, and page, dialog and hero titles 12. Typography presets keep independent font faces and multilingual fallback. Passive tags wrap inside their allocated width; interactive chips retain native focus and input semantics.

The durable `appearance.control-skin` slot resolves a Control Set. V1 root radius, border width, padding, height, scrollbar size and state colors remain the fallback for every family. Optional `spec.families` objects override those values for `button`, `icon-button`, `chip`, `tab`, `input`, `toggle`, `slider` and `scrollbar`. Shared factories, native-control resources and weak registrations apply the same plan and refresh live controls. Primary accent, danger, ghost transparency and checked selection remain semantic engine colors. Commands, keyboard/pointer behavior, accessibility and selection authority remain engine-owned; signature Living Navigation remains its own component. Settings and the library display an actual miniature control sheet.

The canonical Control Styles are Precision, Contour and Halo. Their persisted precision, sculpted and luminous identifiers remain stable. Built-in controls use solid flat fills without relief; their corners, borders and tab edges distinguish the three styles. Control height accepts 24-48 DIP, including per-family overrides. Earlier built-in Control Style IDs normalize to these three. Root and per-family `material` accepts `flat`, `bevel`, `inset` or `luminous`; `relief` is bounded to 0–0.45 and changes the native control's layered gradient fill, including hover/pressed resources. `edge` accepts `all`, `bottom` or `leading` for actual border thickness. Slider `thumbWidth` and `thumbHeight` are 12–28 DIP. The shared projection updates existing native thumb parts on live switching because their template dimensions do not rebind resource assignments automatically. Families inherit undeclared values. Native focus, disabled, commands, checked accent/danger semantics and keyboard/pointer ownership remain unchanged. The preview sheet shows all eight families plus disabled, hover, pressed and focused examples. Shared neutral surfaces project Theme card personality through flat, glass or graduated material brushes independently of Control Style selection.

An Effect declares compatible engine target ids or `all`, one of environment/particles/lighting/surface/interaction channels, and full/reduced/fallback variants. Channels remain pack/compiler metadata. Ordinary built-in runtime has one layer per Home (`effect.home`, surface-scoped), Profile (`effect.profile`, Profile-scoped), and Card (`effect.card`, Profile-scoped), with None defaults and the curated choices listed above. Compatible user effects must support the selected target. Effects never cascade. The extended slot registry remains available to packs. Banner attaches a separate Profile-scoped effect. The Banner picker offers compatible non-interaction pack effects alongside the built-in options and applies the Profile's intensity setting. Effects render above the background/art base and below all foreground content without intercepting input.

Profile Customize is a right inspector over the live surface. Its Media launchers (Cover, Banner, Frame and Media) are grouped first, followed by the engine-owned 3D Figure control; Layout, Backdrop and Profile Effect follow as inline choices. The Layout group also owns the Profile-wide Recent media visibility toggle; it is not duplicated per layout definition. Cover, Banner, Frame, Media and Figure open bounded floating forms sharing the same PreviewSession. Floating Save publishes that shared session and closes Customize after success, without another inspector Save. Other floating forms use Back to return while preserving staged state; Figure Back restores only the Figure choice present at chooser entry. Clicking a Figure model directly selects it in the draft, with no additional confirmation button. Closing or clicking outside the inspector closes immediately when clean and asks before discarding when dirty. Profile editing waits for the authoritative subject snapshot before exposing choices. Card's separate Customize Card action row opens an independent floating form for profile.card and effect.card with one real Card preview. Card customization is absent from the Profile inspector.

Live and preview identity geometry use finite allocation independent of the native Figure child's desired size. Legacy layouts use the shared Profile header to allocate space to Figure and identity/actions. Modular layouts can allocate an independent Figure region. Wide, left-aligned bottom-overlap layouts may place Figure in front of the Banner but confine identity, actions and Banner tags to the opposite column; other wide layouts use separate figure and identity columns, and narrow layouts stack Figure after the identity. Layout switching and modal occlusion retain the existing viewport/camera while hiding or suspending it as needed; pack geometry cannot turn native hosting into an unbounded measure/arrange loop. Figure framing, pedestal, orbit/pan/zoom and texture-quality policy remain engine-owned and do not add pack parameters. The native Figure lease uses a windowless DirectComposition swapchain with premultiplied BGRA and an alpha-zero clear. The durable Thumbnail is hidden only while the live lease is ready, so its baked background cannot cover the Profile Backdrop; fallback retains the Thumbnail. Native input, occlusion and resource retirement remain owned by the existing lease.

Particle layers additionally support `pixel`, `sparkle` and soft radial `bokeh` shapes, horizontal `left`/`right` directions, `drift` from 0–0.2, `twinkle` from 0–0.5, and `spawnRegion` of `all`, `upper`, `lower` or `edges`. These values compile directly into bounded particle placement, opacity and geometry. No new attachment targets are added. Fireflies, Rain, Snow, Bubbles, Light Sweep and Wave are the general built-in Effects; Film, Glitter and Shimmer target Banner only; compatible user packs still use the same renderer contract. The primary catalog is the single source of built-in discovery and procedural prewarm membership.

Effects contain only approved engine primitives, with at most three layers, eighty particles, and combined layer opacity 0.6. Interaction-only definitions require pointer or focus activity. Weather, particles, fog, lighting, flow, pattern, grain, celebration and component decorations share bounded primitives; user shaders/scripts and media sources are prohibited. Non-None effects retain visual continuity at Reduced and static Fallback tiers. The compiler derives missing or empty effect tiers from Full with fewer particles and lower speed, then freezes the static representation. At startup Naut prepares the curated built-in effects across full/reduced/fallback variants using bounded off-screen procedural draws. Prewarm only primes renderer/JIT/resource paths; it is never a delayed admission gate. Settings Effect preview uses the same renderer as runtime. Loaded visible hosts share a scheduler ordered by surface, active Card, visible Card, selected preview, visible preview and Settings preview. Six hosts receive Full quality; additional visible runtime hosts retain Reduced effects. At most three preview hosts animate, with remaining previews rendering static fallback. Hidden/offscreen/unloaded hosts and suspended warm surfaces stop. Clocks are capped at 30 FPS (15 at Reduced tier). The None state creates no backdrop layer subscriptions. OS Reduced Motion uses the static fallback.

Pack diagnostics are available in **Settings → Presentation → Packs / Advanced** and Vault Health. Everyday Presentation library views show previews, asset identity and provenance for reference only; selection occurs in Appearance or the appropriate Customize panel, never by clicking a library card. The headless probe `pwsh ./scripts/verify.ps1 -Scope Presentation` validates catalog/defaults/assets, compatibility, independent selection, preview transactions, restart, repository examples, legacy Theme migration, tier continuity, visual-quality policy, calm tint separation and managed pack lifecycle in a disposable draft-Profile Vault. Use `-Offline` for restore from the local package cache without network sources. Pass `-BuiltInAssetsRoot dist/naut/runtime/Assets/Presentation/BuiltIn` to exercise packaged assets with the current source runtime. `pwsh ./scripts/verify.ps1 -Scope Theme` checks active token consumers and retired selection boundaries; compile and build invoke that internal contract. These checks provide no interaction or playback acceptance.


### Separate Banner stills and image fallback (Naut 0.0.6)

Card image nodes accept optional `fallback: identity` (default, preserving existing packs) or `fallback: none`. The latter leaves the image empty when its semantic source is unavailable instead of substituting the other identity image. A Banner node with `fallback: none` always uses Banner framing. Gallery cards and Home spotlight now resolve the canonical thumbnail belonging to the selected public HOVER video's media, independently of the selected Cover. Missing thumbnails do not generate media on the UI path.

Black Dossier's Dual Identity card preserves distinct Cover and Banner sources through the no-fallback Banner policy. Subject Dossier retains the native Figure header and declares the v1 `actions` semantic; the app places the actual Delete Profile control in the shared page footer after all modules. See `packs/black-dossier/README.md` for complete selection instructions. Gallery reserves a 34-DIP Customize Card action row in each measured item envelope, including virtualized Pinboard bounds. Card artwork retains its authored aspect ratio within that envelope; Catalogue arranges artwork beside actual Profile metadata. Built-in Identity outlines the name and category glyphs rather than boxing them, uses one centered metadata baseline and badge typography for rating and item count, and retains its existing translucent item-count surface. Rating stars and favorite hearts use filled semantic glyphs on all built-in and installed-pack Card layouts, without adding icon boxes; active favorite is red, while inactive favorite uses the neutral on-media or secondary text color. Poster shows a centered Cover/frame and name at rest, then reveals the existing hover details and Banner motion. The Portrait Figure overlays the Banner from the shared header grid, not a clipped negative-margin identity child. The action consumes its tap and reads the currently bound Profile, preserving virtualized recycling and keeping Card and Gallery customization separate.


## Video backdrop continuity

Direct MP4 intake prepares a validated PNG poster, bounded to 1280 pixels per dimension without upscaling, inside the same staged Presentation Pack. Full places video above that poster; Reduced and Fallback use the poster alone. Installation, export, removal and persistence retain the ordinary pack authority. Existing installed manifests are not rewritten: a video backdrop with empty static variants uses a bounded one-frame decode in memory through `SkVideoView` and retains its last valid frame on later tier transitions. No Vault media or permanent runtime cache is created.

The composited decoder emits orientation-aware frames within 1600×900 (900×1600 portrait) at 24 FPS, with one publication in flight. FFmpeg produces bounded opaque BGRA BMP frames; validated uncompressed rows copy directly into a reused Skia bitmap instead of invoking the BMP codec per frame. The retained 24-bit decoder handles compatible fallback frames; native bitmap ownership remains explicit. Playback timing starts at first-frame readiness and rebases after a delayed UI turn instead of bursting catch-up publications. Equivalent backdrop layer segments and video sources survive reconstruction; inactive and suspended surfaces stop decoding while retaining their frame. Video-only backdrops do not also subscribe to the procedural frame clock. Unload, source replacement and shutdown retire frames and processes; the shutdown admission seal remains authoritative.

## Home framed rails

Home portrait rails resolve `profile.frame` with each item's `PresentationContext.ForProfile`, its committed appearance, the current preview bindings, and `CardDataFactory.AppearanceFor`. `HomeProfileTile` uses `CoverFrameView` for both runtime and Home preview. Its square stage preserves silhouette, artwork aperture, frame proportions and Cover transform; labels and favorite state occupy a separate caption area. Poster/tile styles stack captions below; strip/compact styles place them beside the portrait. Tiles remain native keyboard-accessible buttons inside the pooled horizontal repeater.

Recycling and rebinding clear frame, appearance and media transform before loading the new Profile. Asynchronous appearance reads check both tile binding version and presentation generation. Frame/pack notifications refresh realized items in place; Cover/Banner changes retain their existing data refresh. No global Home Frame selection or additional persistence exists.

### Global presets

Settings → Appearance → Global Presets applies compatible installed Contract 4 collections as a single binding transaction. Theme, typography, control skin and individual surface components remain separately selectable. The preset supplies global defaults for Profile visual slots without bulk rewriting Profiles or changing their media; explicit Profile bindings and non-default legacy choices retain priority. A legacy value identical to the built-in default is treated as inherited. Cancel writes nothing. Selecting the built-in preset clears inherited Profile visual definitions while preserving independent state payloads, including Home Recently. An explicit component Reset retains its own established reset semantics.

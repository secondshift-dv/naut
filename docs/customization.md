# Customization

Naut separates collection data from presentation choices. You can change how Home, Gallery, Cards, and Profiles look without changing the underlying media.

## Appearance

**Settings → Appearance** is where users select the installed Theme, Typography and Control Style. Naut provides three curated primary themes (Obsidian, Logbook, Foundry) and three distinct built-in Control Styles. **Settings → Presentation** opens on **Packs / Advanced** for pack installation and management. The other tabs list individual presentation resources.

Use the contextual **Customize** action on Home, Gallery, Cards or Profiles to apply layout, frame, backdrop and effect choices to the relevant surface. The built-in Home, Spotlight, Profile, Card, Media and Gallery categories each offer three curated alternatives. Saved legacy selections resolve through compatibility mappings; installed-pack selections remain independent when their definitions are available.

Presentation Contract 4 adds layered Gallery scenes, responsive navigation and coordinated Global Presets in Appearance. Compatible installed collections can be applied as a single transaction; individual components remain separately selectable. Explicit Profile choices retain priority. Home and Profile support multiple Banner slots with bounded playback.

## Select from pack

Open **Customize Home** or **Customize Profile**, choose **Select from pack**, and search your installed packs in the inspector. One selection previews the pack's available components for that editor.

- **Home** fills Layout, Spotlight Layout, Backdrop and Effect.
- **Profile** fills Profile Layout, Media Layout, Backdrop, Profile Effect, Frame, Banner Effect, Card Layout and Card Effect for the Profile being edited.

Naut uses the first compatible definition for each slot. A missing slot keeps its current selection. Try another pack or edit any component individually before choosing **Save Changes**. **Cancel** returns to the saved appearance. Gallery has its own layout picker, and each of its cards follows its Profile's Card choices.

## Home and Gallery

Home and Gallery have their own layout and presentation controls. Gallery arrangement does not replace a Profile's own Card design.

## Profile

Profile customization can control:

- Cover and Banner selection and framing;
- Frame;
- Profile and media layouts;
- Backdrop;
- Profile effects;
- optional 3D Figure selection.

Cover/Banner framing uses the live preview. Changes remain preview state until they are saved. In v0.0.9, the Profile Banner Collection keeps additional alternatives; **Add as Banner** prepares another eligible video while **Save/Cancel** applies or discards presentation selection. Built-in Film, Glitter and Shimmer effects are composited at display time with per-Profile intensity, without changing the original video. Banner effect buttons mark the active choice; moving Film/Glitter/Shimmer layers are more visible, with static reduced-motion fallbacks. Home and Profile use floating Bubbles instead of Aurora, while saved Aurora selections resolve to Bubbles.

## Presentation resources

Naut ships built-in frames, backdrops, effects, typography, and layouts. Advanced users can install declarative Presentation Packs. Technical pack authoring belongs in the [Presentation Pack reference](development/presentation-packs.md).

## Motion and performance

Effects and animated presentation are bounded by Naut's renderer and reduced or suspended when appropriate. Presentation Packs are data/art definitions; they do not execute arbitrary scripts or shaders.

## Optional curated packs

[Browse and download the collection](../packs/README.md), [install a pack](pack-collection.md), or [contribute your own](contributing-packs.md).

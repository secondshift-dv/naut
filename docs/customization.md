# Customization

Naut separates collection data from presentation choices. You can change how Home, Gallery, Cards, and Profiles look without changing the underlying media.

## Appearance

**Settings → Appearance** is where users select the installed Theme, Typography and Control Style. Naut provides three curated primary themes (Obsidian, Logbook, Foundry) and three distinct built-in Control Styles. The **Settings → Presentation** screen lists installed presentation resources and packs; it is a library view rather than the place where every layout is applied.

Use the contextual **Customize** action on Home, Gallery, Cards or Profiles to apply layout, frame, backdrop and effect choices to the relevant surface. The built-in Home, Spotlight, Profile, Card, Media and Gallery categories each offer three curated alternatives. Saved legacy selections resolve through compatibility mappings; installed-pack selections remain independent when their definitions are available.

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

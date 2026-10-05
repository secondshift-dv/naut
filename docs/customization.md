# Customization

Naut separates collection data from presentation choices. You can change how Home, Gallery, Cards, and Profiles look without changing the underlying media.

## Appearance

Settings provides the application Theme and language. Presentation resources are managed separately so a theme change does not silently replace Profile media or layout choices.

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

Cover/Banner framing uses the live preview. Changes remain preview state until they are saved.

## Presentation resources

Naut ships built-in frames, backdrops, effects, typography, and layouts. Advanced users can install declarative Presentation Packs. Technical pack authoring belongs in the [Presentation Pack reference](development/presentation-packs.md).

## Motion and performance

Effects and animated presentation are bounded by Naut's renderer and reduced/suspended when appropriate. Presentation Packs are data/art definitions; they do not execute arbitrary scripts or shaders.

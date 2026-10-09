# 3D Figures

A compatible 3D model can become an optional interactive **Figure** inside a Profile. Figure presentation extends the normal collection experience; it is not required to use Naut.

## What a Figure is

A suitable model can be prepared into Naut's durable Figure representation and displayed in the Profile identity region.

The Figure viewer supports direct interaction such as **rotate, pan, and zoom** while Naut keeps camera behavior, framing, pedestal presentation, texture policy, and renderer behavior under the application engine.

## Supported formats

Naut v0.0.6 can prepare **GLB, FBX, OBJ, STL and 3MF** as Figures. They display a static scene with rotation, pan and zoom. Animation playback and rig editing are not included; your original file is preserved.

Import the model into a Profile, wait for preparation, then choose it in **Customize > 3D Figure**. Keep OBJ material and texture files together when importing. For FBX, use embedded textures; unrecorded external texture dependencies cannot be loaded.

FBX, OBJ, STL and 3MF conversion also requires the [Microsoft Visual C++ v14 x64 runtime](system-requirements.md#native-model-conversion).

See [Model formats](model-formats.md) for supported materials, package dependencies and preparation limits.

## Hardware tier

The Figure renderer uses **Direct3D 11**.

- Normal Naut use is supported on integrated graphics.
- Interactive Figure presentation can also run on modern integrated graphics.
- A stronger integrated or discrete GPU provides additional headroom for complex models and smoother interaction.
- A discrete GPU is **not** a minimum requirement for Naut.

Normal viewing uses textures up to **2048 × 2048**. Close zoom can refine to **4096 × 4096**; the Reduced tier uses up to **1024 × 1024**. Larger imported textures are prepared into these bounded runtime sizes.

See [System Requirements](system-requirements.md) for the v0.0.6 minimum and recommended tiers.

## If a Figure is not available

A Profile can still use all normal image/video, presentation, and collection features. Figure eligibility and preparation are separate from whether the Profile itself is usable.

# 3D Figures

A compatible 3D model can become an optional interactive **Figure** inside a Profile. Figure presentation extends the normal collection experience; it is not required to use Naut.

## What a Figure is

A suitable model can be prepared into Naut's durable Figure representation and displayed in the Profile identity region.

The Figure viewer supports direct interaction such as **rotate, pan, and zoom** while Naut keeps camera behavior, framing, pedestal presentation, texture policy, and renderer behavior under the application engine.

## Hardware tier

The Figure renderer uses **Direct3D 11**.

- Normal Naut use is supported on integrated graphics.
- Interactive Figure presentation can also run on modern integrated graphics.
- A stronger integrated or discrete GPU provides additional headroom for complex models and smoother interaction.
- A discrete GPU is **not** a minimum requirement for Naut.

Runtime Figure textures are bounded to **2048 × 2048**, including when imported source textures are larger.

See [System Requirements](system-requirements.md) for the v0.0.1 minimum and recommended tiers.

## If a Figure is not available

A Profile can still use all normal image/video, presentation, and collection features. Figure eligibility and preparation are separate from whether the Profile itself is usable.

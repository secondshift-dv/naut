# Model formats and import preparation

Naut prepares GLB, FBX, OBJ, STL and 3MF as interactive Profile Figures. Import a model into a Profile, wait for its Figure preparation to finish, then select it under Customize > 3D Figure. Figures support rotation, pan and zoom. They display a static scene; animation playback and rig editing are not supported. The imported original remains unchanged.

| Format | Preparation |
| --- | --- |
| GLB | Self-contained glTF 2.0 triangle geometry, supported materials and embedded textures use the existing direct preparation path. |
| FBX | Geometry and supported materials convert through the packaged worker. Embedded textures are supported; external textures that are not recorded package components fail with a diagnostic. |
| OBJ | Keep relative MTL files and referenced PNG/JPEG textures beside the OBJ when importing. Missing dependencies prevent complete Figure preparation. |
| STL | Triangle geometry converts to a Figure; STL usually carries no texture or material information. |
| 3MF | The packaged scene converts to a Figure. Unsupported manufacturing extensions or texture representations may fail preparation. |

Conversion runs in a separate process with a two-minute deadline. It reads only declared, SHA-256-verified canonical components, with a 256 MiB package budget, and never downloads textures. Conversion is followed by the same bounded Thumbnail and NFIG preparation used for GLB. Unsupported or oversized scenes finish with a diagnostic rather than indefinite loading. NFIG provenance references the original model hash, not the temporary GLB hash. Temporary conversion bytes are removed after preparation.

Existing supported models with missing Figure assets are queued once by normal startup recovery. Active and failed preparation receipts preserve the existing retry authority. For older schema-v1 Vaults, initialization atomically refreshes only the four recognized legacy GLB-only Figure trigger predicates. Tables and catalog rows are unchanged; unrecognized trigger definitions fail closed.

Candidate import readiness uses metadata and identity capabilities available before confirmation. Thumbnail, Hover and ModelRender remain required according to their contracts after canonical domain commit. A terminal optional face-analysis failure does not keep the preparation spinner active. This phase distinction fixes MPG/MPEG imports that had already finished their candidate jobs but remained at Preparing media.

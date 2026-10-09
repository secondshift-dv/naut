# Naut v0.0.6 System Requirements

These requirements describe the current Windows x64 implementation and build target. Figure / interactive 3D is an optional workload tier; Naut is intended to remain usable without high-end discrete GPU hardware.

| Area | Minimum | Recommended |
| --- | --- | --- |
| **Supported OS** | Windows 10 64-bit or Windows 11 64-bit | Windows 11 64-bit |
| **CPU** | x86-64, 2 cores | Modern x86-64, 4 cores or more |
| **RAM** | 8 GB | 16 GB |
| **GPU** | Integrated graphics sufficient for normal Naut use | Direct3D 11-capable integrated or discrete GPU for smoother Figure workloads |
| **Display** | 1280 × 720 | 1920 × 1080 or higher |

## Storage

Keep at least **3 GB free** for the Naut application, update staging, and rollback. The Vault is separate and needs additional space according to the size of your collection.

An SSD is recommended for the application and Vault, but it is not a functional requirement.

## Video playback

Naut packages its own FFmpeg/ffprobe toolchain for media inspection and prepared media used by the application.

Opening an **original video** from Naut uses the Windows default media player. Playback of that original file therefore also depends on the player and codecs available on the Windows installation.

## Native model conversion

FBX, OBJ, STL and 3MF preparation requires the **Microsoft Visual C++ v14 Redistributable (x64)**. If conversion reports that its native library cannot load, install the current x64 runtime from [Microsoft's official download page](https://learn.microsoft.com/en-us/cpp/windows/latest-supported-vc-redist/), restart Naut, and retry preparation. The .NET runtime is included in the Naut package; this C++ runtime is a separate Windows prerequisite.

## Figure / interactive 3D

Figure uses Direct3D 11 and is optional. Normal Home, Gallery, Profile, Import, Settings, image/video management, and Vault operation do not require a discrete GPU.

Normal Figure viewing uses textures up to **2048 × 2048**; close zoom can refine to **4096 × 4096**, while the Reduced tier uses **1024 × 1024**. More capable graphics hardware provides additional headroom for complex models but is not required for the rest of Naut.

## Release assumptions

Windows x64, the self-contained v0.0.6 package, and Direct3D 11 Figure rendering are implementation constraints. The 2-core CPU, 8/16 GB RAM, and 1280×720/1920×1080 tiers are conservative release-support boundaries; Naut v0.0.6 does not enforce explicit CPU-core, RAM, or display-resolution checks in code.

# Face Intelligence

Naut can use local face analysis to help connect applicable media with existing Profiles. The feature is designed as a **reviewable suggestion pipeline**, not as a silent identity decision.

## What happens during analysis

When face analysis is applicable to imported media, Naut's profiling worker uses the bundled face models locally:

1. **YuNet** detects candidate faces and their geometry.
2. **SFace** produces an embedding for a usable detected face.
3. Naut compares compatible embeddings against the local identity index built from previously confirmed identity samples.
4. Candidates above the configured suggestion policy can be returned as Profile suggestions.
5. A suggestion is still separate from a confirmed face decision.

Images are analyzed directly. For video, Naut works from a bounded sample plan rather than treating every frame as an independent photo.

## Confirmation remains authoritative

Naut keeps face decisions explicitly. Confirming a face can add a usable identity sample for that Profile; rejecting or changing a confirmation updates the stored decision instead of silently rewriting identity history.

This distinction matters:

- **Detection** means a face-like region was found.
- **Embedding** is the model representation used for comparison.
- **Candidate** means a compatible local identity scored strongly enough to be suggested.
- **Confirmed** means the user-facing decision has been accepted for that Profile.

Face evidence can also contribute to Profile relationship evidence after confirmation. A model score by itself is not treated as equivalent to user confirmation.

## Local processing

The YuNet and SFace runtime models are bundled with Naut's profiling worker and this pipeline does not require a cloud face-recognition service. Model availability is treated separately from the rest of the collection: if face analysis is unavailable, normal image/video organization remains a separate capability.

See [Import Media](import-media.md) for where face analysis sits in the broader preparation pipeline.

## Model and license provenance

Naut's runtime inventory pins the exact bundled model artifacts. YuNet is carried under the **MIT** license and SFace under **Apache-2.0**. Full source revisions, hashes, and packaged license locations are documented in [THIRD-PARTY-NOTICES.txt](../THIRD-PARTY-NOTICES.txt).

The README face-intelligence illustration is Naut-authored artwork; it does not reuse OpenCV Zoo demo imagery or OpenCV branding.

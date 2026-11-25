# Performance

Measured performance is recorded here as the benchmark reference for naut. Update this document with new comparable measurements rather than creating a separate report for each release. Performance is not a product guarantee: record the source identity, environment, workload, measurement boundary, integrity checks, and comparison type.

## Measurement contract

- Full-pipeline measurements exercise the native Win32/Uno shell, real import wizard, production registry, scheduler, worker, database, and publication coordinator against an isolated synthetic Vault. Intake and destination selection are separate from the Stage 1-2 interval; Review ends when the wizard is ready; Add to Vault measures the publication action; terminal settlement is recorded separately.
- Component measurements isolate one operation. A matched A/B uses identical component inputs and parameters; a historical comparison does not imply identical cache state, catalog contents, input bytes, or host load.
- Wall time is elapsed time; CPU time is summed process CPU time, including concurrent threads, and may exceed wall time. The UI delay is the excess interval of a 50 ms dispatcher heartbeat, not frame rate or end-to-end input latency.
- Private Bytes, working set, managed allocations, GC committed capacity, estimated managed live bytes, native heap counters, and decoded-image/scene cache bytes measure different, sometimes overlapping resources. Diagnostic forced collections are used only for measurement, not as a product optimization.
- Physical device counters include other processes and deferred writeback. Process I/O counters include cached/logical operations and are not physical disk traffic.

## Native import history

25 November 2026 development measurements, ProductVersion 0.0.37 and catalog schema 3. Each run contained 2,934 candidates, of which 2,842 were destination-owned duplicates and 92 were new media. These are historical measurements with matching item counts, not a controlled full-pipeline A/B.

| Measured source state | Stage 1-2 (s) | Review (s) | Add to Vault (s) | Stage 1-2 UI delay P95 / max (ms) |
|---|---:|---:|---:|---:|
| Before scoped query and finalizer improvements | 399.816 | 2.227 | 48.213 | 16.712 / 687.637 |
| After scoped preparation and publication improvements | 112.716 | 2.193 | 36.594 | 16.715 / 138.472 |
| Before final skipped-candidate query change | 96.146 | 1.739 | 34.017 | 19.165 / 167.209 |

The last historical run recorded 247.719 CPU seconds in Stage 1-2 and 55.250 CPU seconds in Add to Vault. Independent SHA-256 checks covered 5,656 active managed originals and 2,934 input files; all 92 new media had one published owner. SQLite integrity and foreign-key checks passed.

## Current native regression

25 November 2026, uncommitted pre-release development source, ProductVersion 0.0.37, catalog schema 3, after the skipped-candidate settlement change. This is a source-built regression, not a released 0.0.38 package.

**Environment:** Windows 11 build 26300, AMD Ryzen 3 5300U (4 cores, 8 logical processors), AMD Radeon graphics, 10,575,667,200 bytes usable physical memory (9.85 GiB), Samsung MZALQ256HBJD-00BL2 SSD (256 GB). The separate ST1000LM048-2E7172 HDD was not benchmarked. .NET SDK 10.0.401, runtime 10.0.12, Release win-x64 native Uno host, 1100 x 720 window; pinned deployed tools from the existing 0.0.37 package. Face service reported Available.

**Workload:** a freshly seeded, image-only synthetic Vault: 50 profiles, 5,564 active originals, ready thumbnails and profile covers, no historical jobs. All 2,934 candidate files (220,267,235 bytes) match the input hashes from the preceding 96.146 s run. The previous complete starting Vault could not be reconstructed byte-for-byte from retained evidence; this fixture preserves the scale and duplicate distribution but is not a matched catalog or a matched full-pipeline A/B.

| Phase | Wall (s) | Process CPU (s) | UI delay P95 / max (ms) |
|---|---:|---:|---:|
| Intake | 23.354 | 4.969 | 15.091 / 59.336 |
| Destination selection | 0.359 | 0.641 | 70.333 / 85.653 |
| Stage 1-2 | 93.301 | 158.453 | 17.524 / 76.212 |
| Review | 1.252 | 1.281 | 15.465 / 17.349 |
| Add to Vault | 1.404 | 2.922 | 16.664 / 37.465 |
| Terminal settlement | 0.115 | 0.156 | 8.119 / 94.990 |

Add to Vault completed in 1.404 s; the single instrumented skipped-publication operation measured 33.664 ms. The prior 34.017 s publication is a historical observation, not an isolated matched speedup claim.

All 2,934 candidates settled (2,842 skipped, 92 included, zero failed, cancelled or excluded). The unit reached COMPLETED/TERMINAL. Independent verification checked SHA-256 of all 2,934 source files and 5,656 active managed originals, preserved the pre-existing 5,564 media records, and confirmed 92 unique published owners. SQLite integrity was ok, foreign-key failures zero, schema 3.

| Phase | Physical read (MiB) | Physical write (MiB) | End Private Bytes (MiB) |
|---|---:|---:|---:|
| Intake | 77.99 | 100.05 | 243.0 |
| Destination selection | 1.71 | 0.09 | 264.9 |
| Stage 1-2 | 270.86 | 861.95 | 328.7 |
| Review | 0.33 | 0.06 | 336.3 |
| Add to Vault | 0.83 | 5.77 | 341.4 |
| Terminal settlement | 0.05 | 0.00 | 342.3 |

Whole-device counters include unrelated activity. The Governor recorded 88 device samples; observed maximum active admission 1, maximum configured limit 2, and 0 samples exceeding the limit. End-of-phase Private Bytes are point measurements, not peak RAM.

## Component comparisons

### Skipped-candidate settlement

On the same completed synthetic catalog and parameters, the previous exclusive-media read took 31,667.13 ms; materializing eligible active or trashed candidates before the consumer and ownership exclusions took 40.46 ms. Both returned zero rows for the completed duplicate-heavy unit. A separate synthetic exclusion matrix returned identical decisions for active consumers, reused media, interests, published relations, terminal consumers, retired media, and both skip-filter settings. This is a matched query comparison, not a full-pipeline duration.

### Foreground image decoding

Matched ABBA comparison of 500 WebP decodes per pass, same input set and target decode size. Managed bytes allocated on the decode thread:

| Stream buffer | Pass 1 (bytes) | Pass 2 (bytes) |
|---|---:|---:|
| 64 KiB | 33,801,240 | 33,776,248 |
| 4 KiB | 3,069,112 | 3,068,888 |

Both variants decoded all 500 images. Dimensions and decoded pixel SHA-256 matched on 20 comparison images. The first pass had a substantially different cache state; no decode wall-time speedup is claimed.

### Shared video decoder (Contract 4)

25 November 2026: matched ABBA comparison of the packaged v0.0.38 baseline and uncommitted pre-release development source. The baseline assembly hashes were verified against its release inventory. The same synthetic 640 x 360, 24 fps video (SHA-256 `10f20cf94c3731109dbc0f7488861a6163dd4657bf2ea0354622dbc0c3066275`) looped in two 480 x 270 native viewports for approximately 12 s per pass, followed by 6 s idle after stopping. Each variant ran twice. UI delay is the excess over the 50 ms dispatcher heartbeat.

| Playback metric | Baseline 1 | Candidate 1 | Candidate 2 | Baseline 2 |
|---|---:|---:|---:|---:|
| App CPU (ms) | 5625.000 | 4906.250 | 3234.375 | 6578.125 |
| Decoder CPU (ms) | 1250.000 | 546.875 | 500.000 | 953.125 |
| Active decoders | 2 | 1 | 1 | 2 |
| App peak Private Bytes | 180793344 | 178667520 | 178610176 | 180076544 |
| Decoder peak Private Bytes | 55173120 | 27648000 | 27439104 | 55197696 |
| Managed allocated bytes | 8013424 | 4468456 | 4533232 | 8061720 |
| GC collections (Gen 0/1/2) | 4/4/2 | 2/2/2 | 2/2/2 | 4/1/0 |
| GC committed bytes | 19431424 | 19202048 | 19206144 | 21635072 |
| UI delay P95 (ms) | 23.889 | 20.721 | 16.867 | 18.031 |
| UI delay max (ms) | 82.247 | 92.745 | 19.586 | 26.022 |
| Post-stop idle app CPU (ms) | 531.250 | 484.375 | 453.125 | 281.250 |

The mean combined playback CPU decreased 36.2%, decoder peak Private Bytes decreased 50.1%, managed allocation decreased 44.0%, and mean P95 delay decreased 10.3%. App peak Private Bytes decreased 1.0%. Mean maximum delay increased 3.8%; idle app CPU increased 15.4% and both idle samples had zero active decoders. These are matched component measurements with run-to-run variation, not an application-wide speedup or proof of idle improvement. App and decoder peaks are not necessarily simultaneous.


## Navigation and memory

25 November 2026, isolated native shell, 1,100 x 720 window, 30 Profile-to-Import cycles per pass. The Figure-free run traversed 30 distinct profiles cold, warm and repeated, then the same profile 30 times; the Figure run traversed distinct cold, warm and same-profile passes. The Face service reported Available at startup; navigation was not an inference benchmark. These runs measured the navigation workload, not production idle.

| Figure-free pass | Ordinary Private Bytes (MiB) | Diagnostic post-GC Private Bytes (MiB) | Post-GC managed estimate / committed (MiB) | Process CPU (s) |
|---|---:|---:|---:|---:|
| Initial | - | 243.8 | 27.0 / 41.6 | - |
| Distinct cold | 976.3 | 883.8 | 200.0 / 406.1 | 57.016 |
| Distinct warm | 1,172.9 | 930.8 | 203.5 / 439.1 | 34.125 |
| Distinct repeat | 1,259.1 | 1,151.5 | 166.0 / 652.8 | 36.859 |
| Same profile | 760.3 | 724.5 | 157.3 / 225.0 | 23.438 |

The repeat pass increased committed GC capacity while the post-GC managed estimate fell. Native heap allocated was 245.4-257.5 MiB during these passes and the decoded-image cache remained approximately 96 MiB. During a subsequent 30.139 s idle observation, Private Bytes ended at 724.3 MiB, process CPU was 3.391 s and UI-thread CPU was 0.062 s. This is evidence of bounded reuse in the observed workload, not proof of an indefinite memory plateau or the absence of all leaks.

Managed allocation in the latest Figure-free passes was 1,040.0 MiB cold, 995.3 MiB warm, 1,001.5 MiB repeat and 83.0 MiB same-profile.

The earlier Figure-free navigation comparison (same route counts, historical run) recorded diagnostic post-GC Private Bytes of 1,007.8 MiB cold, 1,004.8 MiB warm and 734.7 MiB same-profile. The latest figures above are not an exact byte-matched benchmark.

| Figure pass | Diagnostic post-GC Private Bytes (MiB) | Post-GC managed estimate / committed (MiB) | Process CPU (s) |
|---|---:|---:|---:|
| Initial | 242.9 | 28.7 / 42.0 | - |
| Distinct cold | 1,043.2 | 224.1 / 567.5 | 68.797 |
| Distinct warm | 1,180.1 | 237.9 / 692.0 | 42.859 |
| Same profile | 964.8 | 181.5 / 465.0 | 29.453 |

The Figure run created and released 90 viewports, with zero active viewports and swapchains at completion, one shared device, two cached scenes (48,372,936 bytes), and no reported renderer error. The subsequent 30.138 s idle observation recorded 3.297 s process CPU and 0.109 s UI CPU. The prepared NFIG container and viewport lifecycle regression passed; neither scene bytes nor native heap counters represent all GPU memory.

## Storage observations

The SSD and rotational HDD are separate physical devices. A 72 MiB unbuffered SSD read on the test host recorded 75,497,472 physical read bytes, 73 read requests, zero physical writes, and 0.06354 s elapsed (1,133.08 MiB/s). This component result does not predict import throughput.

The historical 96.146 s pipeline measured 37.03 MiB physical reads and 835.66 MiB physical writes across the SSD, with 35.165 s of device busy time. The 34.017 s Add to Vault interval measured 10.11 MiB physical reads, 14.01 MiB physical writes, and 0.974 s device busy time. The preparation interval was the longest measured lifecycle segment (82.148 s). The Governor recorded 88 full-pipeline samples, with device limit two, maximum one active admission observed, no observed limit violation, and physical queue depth peaking at six. These are whole-device samples, not exclusive process attribution. No physical HDD workload was run.

## Reproduction and limits

The private development benchmark suite includes dispatcher, duplicate, bulk and transfer probes. See the public [build and release reference](development/build-and-release.md) for shipped source verification and canonical package reproduction. The native benchmark harness and synthetic inputs are retained as private development evidence, not distributed with the application.

### v0.0.39 importer component verification

25 November 2026, SDK 10.0.401, synthetic local fixtures, canonical benchmark scenario All. The duplicate matrix matched all expected counts (80 files, 0/25/50/100% duplicate ratios); hash-and-lookup P95 was 17.9/15.8/17.7/48.7 ms respectively. All 27 transfer matrix samples passed byte-integrity checks. Median throughput at the existing 1 MiB buffer/64 MiB checkpoint configuration was 285.04 MiB/s; median standalone hashing was 873.47 MiB/s. The bulk probe transferred 6,476,005,376 bytes across 100 files at a weighted 288.04 MiB/s, with 169 durable checkpoints, matching staged hashes, unchanged sources and successful cancellation cleanup. The mixed-batch probe published 910 of 1,000 candidates (90 owned duplicates) in 130.097 s and 92 of 2,934 candidates (2,842 owned duplicates) in 102.125 s. These are component and synthetic lifecycle observations, not a matched comparison against the previous revision; throughput varied across runs and no zero-regression claim is established.

Native measurements use synthetic sources, a disposable catalog, and a locally compiled test harness. Keep exact input hashes, starting fixture inventory, harness/source identity, logs and integrity checks in private evidence storage outside the repository. Generated media, raw traces, dumps and binaries are not source artifacts. The preserved measurement summaries do not certify production Vault performance, exhaustive CPU/native ownership, an HDD throughput forecast, a signed upgrade, or canonical packaging of uncommitted source.

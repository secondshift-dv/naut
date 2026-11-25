# Performance

Measured 25 November 2026. Tables follow test type and chronological run order; repeated tests of the same version remain separate. Source builds are not release binaries. All measurements use isolated fixtures, not production Vaults.

## Test environment

| Parameter | Recorded condition |
|---|---|
| Native import host | Windows 11 build 26300; Ryzen 3 5300U (4C/8T); Radeon graphics; 9.85 GiB usable RAM |
| Storage | Samsung MZALQ256HBJD-00BL2 256 GB SSD; separate ST1000LM048-2E7172 HDD not benchmarked |
| Native runtime | SDK 10.0.401; .NET 10.0.12; Release win-x64 Uno; 1100 x 720; deployed v0.0.37 tools; Face service Available |
| Time | Wall = elapsed; CPU = summed process CPU; UI delay = excess over 50 ms dispatcher heartbeat |
| Memory / I/O | Private Bytes, managed allocations, GC capacity/live estimate and native/cache counters are distinct; physical I/O includes other processes and deferred writes |

## Native import

2,934 candidates: 2,842 destination-owned duplicates and 92 new media. Historical comparisons, not matched full-pipeline A/B.

| ProductVersion / run | Source state | Stage 1-2 (s) | Review (s) | Add to Vault (s) | Stage 1-2 UI P95 / max (ms) |
|---|---|---:|---:|---:|---:|
| 0.0.37 / 1 | Before scoped queries and finalizer improvements | 399.816 | 2.227 | 48.213 | 16.712 / 687.637 |
| 0.0.37 / 2 | Scoped preparation and publication | 112.716 | 2.193 | 36.594 | 16.715 / 138.472 |
| 0.0.37 / 3 | Before skipped-candidate query change | 96.146 | 1.739 | 34.017 | 19.165 / 167.209 |
| 0.0.37 / 4 | After skipped-candidate settlement change; pre-0.0.38 source | 93.301 | 1.252 | 1.404 | 17.524 / 76.212 |

| Version / run | Phase | CPU (s) | Physical read / write (MiB) | Device busy (s) |
|---|---|---:|---:|---:|
| 0.0.37 / 3 | Stage 1-2 | 247.719 | 37.03 / 835.66 | 35.165 |
| 0.0.37 / 3 | Add to Vault | 55.250 | 10.11 / 14.01 | 0.974 |

### 0.0.37 / 4 - phase breakdown

| Phase | Wall (s) | CPU (s) | UI P95 / max (ms) | Read / write (MiB) | End Private Bytes (MiB) |
|---|---:|---:|---:|---:|---:|
| Intake | 23.354 | 4.969 | 15.091 / 59.336 | 77.99 / 100.05 | 243.0 |
| Destination selection | 0.359 | 0.641 | 70.333 / 85.653 | 1.71 / 0.09 | 264.9 |
| Stage 1-2 | 93.301 | 158.453 | 17.524 / 76.212 | 270.86 / 861.95 | 328.7 |
| Review | 1.252 | 1.281 | 15.465 / 17.349 | 0.33 / 0.06 | 336.3 |
| Add to Vault | 1.404 | 2.922 | 16.664 / 37.465 | 0.83 / 5.77 | 341.4 |
| Terminal settlement | 0.115 | 0.156 | 8.119 / 94.990 | 0.05 / 0.00 | 342.3 |

| Verification | Result |
|---|---|
| Run 3 | Preparation 82.148 s; SHA-256 of 5,656 active originals and 2,934 inputs; 92 new media with one published owner each; SQLite integrity/FK PASS |
| Run 4 workload | 50 Profiles; 5,564 existing images; ready thumbnails/covers; no historical jobs; 220,267,235 input bytes; same input hashes as run 3, different starting catalog |
| Run 4 settlement | 2,842 skipped; 92 included; 0 failed/cancelled/excluded; COMPLETED/TERMINAL; skipped-publication operation 33.664 ms |
| Run 4 integrity | 2,934 source and 5,656 managed hashes verified; 5,564 existing records preserved; 92 unique published owners; SQLite integrity/FK PASS; schema 3 |
| Disk Governor, runs 3-4 | 88 samples per run; max active 1 / limit 2; zero violations; run 3 queue depth peak 6 |
| Comparison limit | Run 4 source-built, not released v0.0.38; end Private Bytes are not peaks; publication improvement is historical, not a controlled A/B |

## Import components

### Skipped-candidate settlement

| Version / state | Query time (ms) | Result |
|---|---:|---|
| 0.0.37 / before settlement change | 31,667.13 | 0 eligible rows |
| 0.0.37 / after settlement change | 40.46 | 0 eligible rows; exclusion decisions identical |

Matched completed synthetic catalog and parameters; active/reused/retired media, consumer, interest, published relation and both skip-filter cases checked.

### 0.0.39 - duplicate lookup

Development source, SDK 10.0.401, scenario All; synthetic 80-file matrix.

| Duplicate ratio | Hash + lookup P95 (ms) | Count verification |
|---|---:|---|
| 0% | 17.9 | PASS |
| 25% | 15.8 | PASS |
| 50% | 17.7 | PASS |
| 100% | 48.7 | PASS |

### Transfer and mixed-batch settlement

| Version | Workload | Measurement | Integrity |
|---|---|---|---|
| 0.0.39 dev | 27 transfer samples; 1 MiB buffer / 64 MiB checkpoint baseline | Median 285.04 MiB/s | All PASS |
| 0.0.39 dev | Standalone hashing | Median 873.47 MiB/s | PASS |
| 0.0.39 dev | Bulk: 100 files; 6,476,005,376 bytes | Weighted 288.04 MiB/s; 169 checkpoints | Staged hashes, unchanged sources, cancellation cleanup PASS |
| 0.0.39 dev | Mixed: 1,000 candidates; 90 owned duplicates | 910 published; 130.097 s | PASS |
| 0.0.39 dev | Mixed: 2,934 candidates; 2,842 owned duplicates | 92 published; 102.125 s | PASS |

These are component and synthetic lifecycle observations, not a matched revision comparison or proof of zero regression.

## Image decoding

Matched ABBA, 500 WebP decodes per pass; identical inputs and target size. Exact ProductVersion not recorded in the measurement summary.

| Stream buffer | Managed allocation pass 1 (bytes) | Pass 2 (bytes) |
|---|---:|---:|
| 64 KiB | 33,801,240 | 33,776,248 |
| 4 KiB | 3,069,112 | 3,068,888 |

All decodes completed; dimensions and pixel SHA-256 matched for 20 images. First-pass cache states differed; no wall-time speedup claimed.

## Shared video decoder - Contract 4

Matched ABBA: packaged v0.0.38 versus uncommitted Contract 4 candidate based on v0.0.38 source (pre-v0.0.39). Verified baseline assembly hashes. Same 640 x 360, 24 fps synthetic video, two 480 x 270 viewports, about 12 s playback and 6 s post-stop idle per pass.

| Chronological run | App CPU (ms) | Decoder CPU (ms) | Decoders | App peak Private Bytes | Decoder peak Private Bytes | Managed allocated bytes |
|---|---:|---:|---:|---:|---:|---:|
| 0.0.38 release / A1 | 5,625.000 | 1,250.000 | 2 | 180,793,344 | 55,173,120 | 8,013,424 |
| Contract 4 candidate / B1 | 4,906.250 | 546.875 | 1 | 178,667,520 | 27,648,000 | 4,468,456 |
| Contract 4 candidate / B2 | 3,234.375 | 500.000 | 1 | 178,610,176 | 27,439,104 | 4,533,232 |
| 0.0.38 release / A2 | 6,578.125 | 953.125 | 2 | 180,076,544 | 55,197,696 | 8,061,720 |

| Chronological run | GC Gen 0/1/2 | GC committed bytes | UI P95 / max (ms) | Post-stop app CPU (ms) |
|---|---:|---:|---:|---:|
| 0.0.38 release / A1 | 4/4/2 | 19,431,424 | 23.889 / 82.247 | 531.250 |
| Contract 4 candidate / B1 | 2/2/2 | 19,202,048 | 20.721 / 92.745 | 484.375 |
| Contract 4 candidate / B2 | 2/2/2 | 19,206,144 | 16.867 / 19.586 | 453.125 |
| 0.0.38 release / A2 | 4/1/0 | 21,635,072 | 18.031 / 26.022 | 281.250 |

| Mean change (candidate vs baseline) | Result |
|---|---:|
| Combined playback CPU | -36.2% |
| Decoder peak Private Bytes | -50.1% |
| App peak Private Bytes | -1.0% |
| Managed allocation | -44.0% |
| UI delay P95 | -10.3% |
| UI delay maximum | +3.8% |
| Post-stop idle app CPU | +15.4% |

Zero active decoders after stopping; app and decoder memory peaks need not coincide. Component results only, not an application-wide speedup or idle improvement claim.

## Navigation and memory

Native shell, 1100 x 720, 30 Profile-to-Import cycles per pass. Exact ProductVersion not recorded in the measurement summary. Earlier and later Figure-free runs are historical, not byte-matched. Face service Available; no inference benchmark.

### Without Figure

| Run / pass | Ordinary Private Bytes (MiB) | Post-GC Private Bytes (MiB) | Post-GC managed estimate / committed (MiB) | Managed allocated (MiB) | CPU (s) |
|---|---:|---:|---:|---:|---:|
| Earlier / distinct cold | - | 1,007.8 | - | - | - |
| Earlier / distinct warm | - | 1,004.8 | - | - | - |
| Earlier / same Profile | - | 734.7 | - | - | - |
| Later / initial | - | 243.8 | 27.0 / 41.6 | - | - |
| Later / distinct cold | 976.3 | 883.8 | 200.0 / 406.1 | 1,040.0 | 57.016 |
| Later / distinct warm | 1,172.9 | 930.8 | 203.5 / 439.1 | 995.3 | 34.125 |
| Later / distinct repeat | 1,259.1 | 1,151.5 | 166.0 / 652.8 | 1,001.5 | 36.859 |
| Later / same Profile | 760.3 | 724.5 | 157.3 / 225.0 | 83.0 | 23.438 |

### With Figure

| Pass | Post-GC Private Bytes (MiB) | Post-GC managed estimate / committed (MiB) | CPU (s) |
|---|---:|---:|---:|
| Initial | 242.9 | 28.7 / 42.0 | - |
| Distinct cold | 1,043.2 | 224.1 / 567.5 | 68.797 |
| Distinct warm | 1,180.1 | 237.9 / 692.0 | 42.859 |
| Same Profile | 964.8 | 181.5 / 465.0 | 29.453 |

| Post-navigation observation | Without Figure | With Figure |
|---|---|---|
| Idle interval (s) | 30.139 | 30.138 |
| Idle process CPU (s) | 3.391 | 3.297 |
| Idle UI CPU (s) | 0.062 | 0.109 |
| End idle Private Bytes (MiB) | 724.3 | - |
| Native heap / decoded-image cache (MiB) | 245.4-257.5 / approx. 96 | - |
| Renderer lifecycle | - | 90 viewports created/released; 0 active viewports/swapchains; 1 shared device; 2 cached scenes (48,372,936 bytes); no reported error; NFIG lifecycle PASS |

Repeat GC capacity rose while estimated live bytes fell. Forced GC is diagnostic only; bounded reuse in these runs does not prove an indefinite plateau, leak freedom or total GPU memory.

## Storage component

| Device / workload | Physical read | Read requests | Physical write | Wall (s) | Throughput |
|---|---:|---:|---:|---:|---:|
| SSD / 72 MiB unbuffered read | 75,497,472 bytes | 73 | 0 | 0.06354 | 1,133.08 MiB/s |
| HDD | Not measured | - | - | - | - |

## Reproduction and limits

| Test / source | Reproduction |
|---|---|
| Native import and video | Native synthetic fixtures; verified v0.0.38 video baseline; pre-release source candidates. Private harness and synthetic evidence are not distributed. |
| Import components | Private development benchmark suite: dispatcher, duplicate, bulk and transfer probes. |
| Public build | See [build and release reference](development/build-and-release.md) for shipped source verification and canonical packaging. |

Measurements do not certify production Vault performance, HDD throughput, exhaustive CPU/native ownership, signed upgrades or canonical packaging of uncommitted source. Generated media, traces, dumps and binaries are not source artifacts.

# Local inference catalog provenance

The supported hardware, runtime, model, and recipe selections in the local
inference catalog incorporate data adapted from the NVIDIA CAIR recipe catalog,
copyright NVIDIA Corporation and licensed under
[CC BY 4.0](https://creativecommons.org/licenses/by/4.0/).

Changes made for OpenClaw include representing recipe data as typed Windows
records, limiting selection to explicitly supported hardware, and replacing
mutable artifact references with independently verified public GitHub and
Hugging Face revision and SHA-256 pins. A public CAIR source URL is expected but
was not available when these records were prepared. No non-public location is
recorded in source or generated state.

The validated CAIR archive used for this implementation was 757,655 bytes with
SHA-256 `d0f14b1bccceb71b5cc0f1bf29e4ed427e24705b44939f074d2c4e86c1d17549`.
The source archive remains a local, ignored reference and is not redistributed
by this repository. OpenClaw translates its Windows llama.cpp settings into
typed records, keeps a recommended default with explicit user-selected
alternatives, and independently pins public immutable runtime and model
artifacts. CAIR's mutable URLs, experimental/runnable flags, and generated
catalog files are not used at runtime.

## Pedro Larroy PR #1174

Hardware work was evaluated from
[openclaw-windows-node PR #1174](https://github.com/openclaw/openclaw-windows-node/pull/1174),
source commit `cbeb376db6131fc8432b920c7a1949b3ff4ba46e`, authored by Pedro
Larroy `<plarroy@nvidia.com>` on 2026-08-17.

The following non-overlapping slices were imported byte-for-byte with Pedro's
original author identity and author date. Joel Fernandes remains the committer.
The commits retain the source PR, source commit, full and slice patch IDs, and
the original Claude co-author trailer.

| Imported concern | Target commit | Slice patch ID |
|---|---|---|
| Physical-memory probe and DeviceStatusProvider use | `caa8925e` | `8de118a7e8a44289494137c7b2256791124827ac` |
| Policy-neutral host hardware value types | `7655b448` | `945f399708d9eb3021440a1cac745a7c9828b078` |

The following PR areas were deliberately not imported because they overlap the
implemented OpenClaw design or weaken its qualification boundary:

- `HardwareProbe` and `NvidiaSmiParser`: replaced by the trusted absolute-path
  NVML probe, which supplies stable adapter identity and total/free memory
  without executing a PATH-resolved process.
- `BackendSelector` and its CPU, Vulkan, and CUDA fallback rules: replaced by
  the fail-closed qualified Windows recipe selector. OpenClaw does not silently
  fall back to CPU, Vulkan, another CUDA generation, or an unqualified SKU.
- `ModelRecommender`: replaced by exact SKU/architecture recipes and measured
  full-context FP16 KV requirements. VRAM is not summed across unqualified
  multi-GPU topologies and free memory does not silently change the model.
- Pedro's backend/model catalogs and WIP plans: replaced by the validated CAIR
  settings plus independently pinned llama.cpp and immutable Hugging Face
  artifacts. This avoids mutable model URLs, unpublished entries, stale
  context assumptions, and policy duplication.
- The unrelated build-dependencies skill commit was omitted from this feature
  series.

All integration, NVML probing, qualification policy, setup, downloads,
llama-server lifecycle, WSL networking, gateway configuration, and UI work are
Joel-authored commits. Pedro attribution is limited to the exact retained
slices above.

# Native llama-server Local AI proof

This bundle records a clean-room OpenClaw onboarding run on 2026-08-18. The
code-under-test was `702caa43`; the post-run health timestamp regression was
fixed and revalidated at `7d3577c6`. Nothing was pushed.

## Outcome

- Hardware: NVIDIA RTX Spark N1X, Windows Arm64, driver 616.00, 24,512 MiB
  GPU-visible memory.
- Runtime: pinned llama.cpp `b10488`, native Windows Arm64 CUDA 13.4.
- Model: `unsloth/Qwen3.6-35B-A3B-MTP-GGUF` at immutable revision
  `5bc3e238d916f48a861bac2f8a1990a0e9b7e98d`.
- Model file: `Qwen3.6-35B-A3B-UD-Q4_K_M.gguf`, 22,663,387,424 bytes,
  SHA-256 `0b21525e972670ed59e1812e170b27c26355381f0656ecc4e25617ece7dac58b`.
- Recipe: 262,144 context, FP16 K/V cache, flash attention, one slot, full
  CUDA offload, request-driven loading.
- Gateway provider: OpenAI-compatible `llamacpp` provider at the persisted
  native endpoint, selected as the primary OpenClaw model.
- Primary chat returned the exact requested response:
  `OPENCLAW LLAMA SERVER READY`.

The companion starts the lightweight llama-server router at launch. It does
not preload the model. The first request starts the model worker; exiting the
companion removes both router and worker through the Windows Job Object.

## Visual evidence

| File | Evidence |
|---|---|
| `01-welcome.png` | Real clean-run welcome screen. |
| `02-model-choice.png` | Deterministic DEBUG capture of the same build's hardware detection, recommended default, alternate-model chooser, immutable Hugging Face source, and recipe summary. |
| `03-wsl-consent.png` | Real clean-run explicit consent for the global mirrored-networking change and one-time WSL shutdown. |
| `04-model-download.png` | Deterministic DEBUG capture of the same build's byte-based Hugging Face download progress. |
| `05-complete.png` | Real clean-run completion screen with llama-server, model, context, GPU offload, gateway, pairing, and startup status. |
| `06-local-ai-unloaded.png` | Real companion state after restart: router running, model downloaded and verified but unloaded. |
| `07-local-ai-loaded.png` | Real companion state after primary chat: model loaded on GPU. |
| `08-primary-chat.png` | Real OpenClaw primary-chat request and exact local response. |
| `walkthrough.mp4` | 63.7-second edited walkthrough, SHA-256 `ec97f3d4f5b6e56b8698631cc80f562ea59290cfff973d101437ea575c02f55a`. |

The desktop recorder did not composite the setup window during the long model
download. The edited walkthrough therefore uses the real welcome, consent,
completion, unloaded, loaded, and chat captures; the chooser and progress
frames use the repository's DEBUG-only deterministic XAML capture route from
the same build. The final 19.7 seconds are the real post-onboarding screen
recording, cropped to the companion window so unrelated terminal content is
not included. No preview frame is represented as a live download measurement.

## Performance

`benchmark.json` contains three request-cold and three hot streamed requests.
Before each cold sample, the companion was restarted and the router API was
required to report zero loaded models. TTFT is the first streamed content or
reasoning token. TPS is completion tokens after that first token divided by
the remaining stream time.

| State | TTFT runs | Median TTFT | TPS runs | Median TPS |
|---|---:|---:|---:|---:|
| Request-cold | 34.514, 32.641, 31.879 s | 32.641 s | 76.14, 77.74, 79.35 | 77.74 |
| Hot | 0.155, 0.129, 0.177 s | 0.155 s | 88.57, 87.99, 87.46 | 87.99 |

These are process-cold results with the Windows file cache available, not
reboot-cold measurements. The cold delay is model-worker creation and loading;
the router itself was already healthy.

## GPU and process evidence

During the primary-chat load:

- llama-server reported all 42 of 42 layers offloaded.
- The worker's WDDM counters showed 24,531,591,168 dedicated bytes and
  12,266,242,048 shared bytes.
- The router showed 142,770,176 dedicated bytes and 67,108,864 shared bytes.
- `nvidia-smi` showed 24,244 MiB used of 24,512 MiB.
- The server log reported 21,087.70 MiB CUDA model buffer, 5,120.00 MiB CUDA
  KV buffer, and 2,592.13 MiB CUDA compute buffer.
- The companion was the router parent, and the router was the worker parent.
  Stopping the companion removed both listeners. Relaunch restored the same
  persisted endpoint with the model still unloaded.

Machine-readable values are in `runtime-evidence.json`.

## WSL2 QA

The clean run began with only the unrelated `Ubuntu-24.04` distro and no
`.wslconfig`. Its sentinel SHA-256 was
`5b4d29711620cc60ded1c0a84a883df8ce1f3f3a746fd0893b864ff4b511a71c`
before and after testing.

Verified:

- Declining mirrored-network consent made no setup mutation and did not stop
  or delete the unrelated distro.
- Accepting consent wrote mirrored networking and performed one global WSL
  shutdown before creating only `OpenClawGateway` as a WSL2 distro.
- The native endpoint was healthy from Windows and from `OpenClawGateway`.
- The gateway service was enabled and active and primary chat reached the
  exact local provider/model.
- Restarting the gateway preserved the native llama-server processes.
- Terminating only `OpenClawGateway` preserved native inference and the
  unrelated distro.
- A global `wsl --shutdown` preserved native inference; the companion
  recovered the app gateway without starting the unrelated distro.
- Injected setup failures rolled back the app distro, mirrored-networking
  change, native processes, partial downloads, and generated router preset.
- The unrelated Ubuntu registration and sentinel survived every path.

Not verified on this host:

- The Windows-feature-enable and reboot-required path from a VM with WSL and
  Virtual Machine Platform disabled.
- A physical Windows reboot after successful onboarding.
- A real discrete RTX 5090 or RTX PRO 6000 machine. Their chooser behavior,
  architecture mapping, pins, and setup paths are covered by automated tests.

## Automated validation

Code validation at `7d3577c6`:

- `build.ps1`: all five projects passed.
- Shared: 3,783 passed, 32 expected skips, 0 failed.
- Tray: 2,642 passed, 0 failed.
- SetupEngine: 988 passed, 0 failed.
- Connection: 698 passed, 0 failed.
- Post-fix isolated relaunch: 150 new log lines, zero
  `LastCheckTime must be written on UI thread` warnings, and the model remained
  unloaded. The relaunch intentionally followed a forced proof-process stop,
  so the pre-existing unclean-session marker and one transient gateway
  handshake retry are not presented as product-clean shutdown evidence.

Final proof tree validation after capture sanitization:

- Documentation validation: 48 Markdown files passed.
- Both JSON evidence files parse successfully.
- The walkthrough is a 63.7-second, 1920x1080, 30 fps H.264 MP4 and its
  committed SHA-256 matches the value above.

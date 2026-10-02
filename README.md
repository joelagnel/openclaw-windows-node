# PR #1595 evidence

Screenshots are cropped to the OpenClaw window only; no desktop, taskbar, or
other applications are captured.

## spark-48gb-chat.png
RTX Spark 48GB SKU serving the new recipe: `qwen3.6-35b-a3b-mtp-ud-q4-k-s`,
selected in the composer as `Qwen3.6 35B-A3B (UD-Q4_K_S)`.

## dell-5090-verified.png
Dell RTX 5090 setup completion, `Verified model: llamacpp/qwen3.8-27b-mtp-ud-q4-k-m`.

## dell-published-install-upgrade.png
Branch head installed over the published alpha (`76ab8399`, `2026.9.5.7`) with no
purge and no package removal. Serving inference on the install the alpha created:
`Assistant - qwen3.8-27b-mtp-ud-q4-k-m - 0/196.6K`, where 196.6K matches the
retained receipt's `"contextLength": 196608`.

The backing process is
`...\LocalAI\engines\llama-server\b11026\win-x64\llama-server.exe` -- the runtime
the alpha installed, retained across the upgrade. The 16,464,440,224 byte model
file kept its original `2026-09-24T10:07:05` mtime, so it was not re-downloaded.

## spark48-recipe-b11320.png

Current-head proof on the RTX Spark 48GB SKU (arm64), branch head `b57027f6`
rebased onto `main`. The managed runtime directory was purged before installing,
so b11320 was re-acquired and every pinned runtime file digest was verified.

Receipt after install:
`engineVersion=b11320  runtimeId=b11320-cuda13-arm64  model=qwen3.6-35b-a3b-mtp-ud-q4-k-s`

The screenshot shows a real prompt and response served by that recipe:
`Assistant - qwen3.6-35b-a3b-mtp-ud-q4-k-s - 15.3K/98.3K (15%)`, with the
composer reading `Qwen3.6 35B-A3B (UD-Q4_K_S)`.

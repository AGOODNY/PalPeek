# Third-party components

PalPeek is GPL-3.0 software.

- Sunshine: `LizardByte/Sunshine`, pinned to `v2026.516.143833`, GPL-3.0.
- Moonlight PC: `moonlight-stream/moonlight-qt`, pinned to `v6.1.0`, GPL-3.0.
- hls.js: `video-dev/hls.js`, pinned to `v1.6.16`, Apache-2.0. The minified
  browser build is packaged locally at `src/PalPeek.App/Web/hls.min.js`
  (SHA-256 `442F599C34F103C3355B375A23BDFF560592D7117D09A8C847242EA3DE2D40E0`).

The bundled Sunshine build is a PalPeek-specific fork. Its Windows-only
changes add local IPC, HWND-scoped Windows Graphics Capture, process-tree
audio capture, and hard-disable all remote input paths. These changes remain
licensed under GPL-3.0; they do not add a new license or linking exception.

Redistributed builds must include corresponding source or a durable written
offer. Release automation must publish source archives beside installers.

# Third-party components

PalPeek is GPL-3.0 software.

- Sunshine: `LizardByte/Sunshine`, pinned to `v2026.516.143833`, GPL-3.0.
- Moonlight PC: `moonlight-stream/moonlight-qt`, pinned to `v6.1.0`, GPL-3.0.

The bundled Sunshine build is a PalPeek-specific fork. Its Windows-only
changes add local IPC, HWND-scoped Windows Graphics Capture, process-tree
audio capture, and hard-disable all remote input paths. These changes remain
licensed under GPL-3.0; they do not add a new license or linking exception.

Redistributed builds must include corresponding source or a durable written
offer. Release automation must publish source archives beside installers.

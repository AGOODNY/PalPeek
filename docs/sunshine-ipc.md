# PalPeek Sunshine IPC protocol

PalPeek controls its Sunshine fork through the local Windows named pipe
`\\.\pipe\PalPeekCapture`.

## Transport and trust boundary

- The pipe accepts local clients only (`PIPE_REJECT_REMOTE_CLIENTS`).
- Messages are UTF-8 JSON, one object per line, with a 16 KiB request limit.
- Every request includes `"protocolVersion": 1`.
- The pipe does not expose keyboard, mouse, touch, pen, or controller commands.
- A capture target is accepted only when the HWND exists, is visible, and is
  owned by the supplied PID.

## Commands

### `setTarget`

Sets the only permitted video and audio source.

```json
{
  "protocolVersion": 1,
  "command": "setTarget",
  "pid": 1234,
  "hwnd": 5678,
  "appId": "730",
  "name": "Example Game",
  "sessionId": "opaque-session-id",
  "capture": "window",
  "audio": "processTree"
}
```

Repeated requests for the same PID, HWND, and session are idempotent.

### `status`

Returns the current target plus capture, process-audio, and encoder states.

```json
{
  "ok": true,
  "protocolVersion": 1,
  "capture": "targetReady",
  "audio": "idle",
  "encoding": "ready",
  "target": {
    "pid": 1234,
    "hwnd": 5678,
    "sessionId": "opaque-session-id",
    "generation": 1
  },
  "errorCode": null,
  "message": null
}
```

Capture states are `idle`, `targetReady`, `capturing`, and `error`. Audio
states are `idle`, `ready`, `capturing`, and `error`. Encoding states are
`waitingForTarget`, `probing`, `ready`, `streaming`, and `error`.

### `stopSessions`

Stops active Moonlight streaming sessions without terminating the game.

### `sessionEnded`

Stops active streams and clears the target for the supplied session. A stale
session ID is rejected.

### `clearTarget`

Stops active streams and removes the capture target.

### `pair`

Submits a four-digit PIN for a pending Moonlight pairing request.

### `shutdown`

Requests a graceful Sunshine process shutdown.

## Responses and errors

Successful commands return `{"ok":true}` with command-specific fields.
Failures use a stable code and a human-readable message:

```json
{
  "ok": false,
  "error": {
    "code": "invalid_window",
    "message": "The selected HWND is not a visible window owned by the target PID"
  }
}
```

Known codes include `protocol_version_mismatch`, `invalid_window`,
`stale_session`, `invalid_pin`, `pairing_rejected`, `unknown_command`,
`command_too_large`, `invalid_json`, and `internal_error`. Runtime capture
errors are reported by `status` through `errorCode` and `message`.

There is deliberately no desktop or system-audio fallback. If the target
window or process becomes unavailable, the active source fails and the
streaming session is stopped.

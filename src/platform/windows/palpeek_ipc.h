/**
 * @file src/platform/windows/palpeek_ipc.h
 * @brief PalPeek's local-only control channel and capture target state.
 */
#pragma once

#include <chrono>
#include <cstdint>
#include <optional>
#include <string>
#include <string_view>
#include <thread>

#ifndef WIN32_LEAN_AND_MEAN
  #define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>

namespace palpeek {
  constexpr int protocol_version = 2;

  struct capture_target_t {
    DWORD root_pid;
    HWND hwnd;
    std::string app_id;
    std::string name;
    std::string session_id;
    std::uint64_t generation;
  };

  enum class capture_state_t {
    idle,
    target_ready,
    capturing,
    error,
  };

  enum class audio_state_t {
    idle,
    ready,
    capturing,
    error,
  };

  enum class encoding_state_t {
    waiting_for_target,
    probing,
    ready,
    error,
  };

  std::optional<capture_target_t> capture_target();
  bool wait_for_capture_target(std::chrono::milliseconds timeout);
  void set_capture_state(capture_state_t state, std::string_view error_code = {}, std::string_view message = {});
  void set_audio_state(audio_state_t state, std::string_view error_code = {}, std::string_view message = {});
  void set_encoding_state(encoding_state_t state, std::string_view error_code = {}, std::string_view message = {});
  std::thread start_control_pipe();
  void stop_control_pipe();
}  // namespace palpeek

/**
 * @file src/platform/windows/palpeek_host.h
 * @brief PalPeek's local-only control channel and capture target state.
 */
#pragma once

#include <cstdint>
#include <optional>
#include <string>
#include <thread>

#include <windows.h>

namespace palpeek {
  struct capture_target_t {
    DWORD root_pid;
    HWND hwnd;
    std::string app_id;
    std::string name;
    std::string session_id;
    std::uint64_t generation;
  };

  std::optional<capture_target_t> capture_target();
  std::thread start_control_pipe();
  void stop_control_pipe();
}  // namespace palpeek

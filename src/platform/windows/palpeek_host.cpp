/**
 * @file src/platform/windows/palpeek_host.cpp
 * @brief Local named-pipe bridge used by the PalPeek desktop application.
 */

#include "palpeek_host.h"

#include <atomic>
#include <mutex>
#include <string>

#include <nlohmann/json.hpp>

#include "src/logging.h"
#include "src/nvhttp.h"
#include "src/rtsp.h"

namespace {
  constexpr auto pipe_name = R"(\\.\pipe\PalPeekCapture)";
  std::mutex target_mutex;
  std::optional<palpeek::capture_target_t> target;
  std::atomic_uint64_t generation {0};
  std::atomic_bool stopping {false};

  nlohmann::json handle_command(const nlohmann::json &request) {
    const auto command = request.value("command", "");

    if (command == "setTarget") {
      const auto pid = request.value("pid", 0u);
      const auto raw_hwnd = request.value("hwnd", std::uint64_t {0});
      auto hwnd = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(raw_hwnd));
      DWORD window_pid = 0;
      GetWindowThreadProcessId(hwnd, &window_pid);

      if (!pid || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || window_pid == 0) {
        return {{"ok", false}, {"error", "Invalid or invisible capture window"}};
      }

      std::scoped_lock lock(target_mutex);
      target = palpeek::capture_target_t {
        pid,
        hwnd,
        request.value("appId", ""),
        request.value("name", ""),
        request.value("sessionId", ""),
        ++generation
      };
      BOOST_LOG(info) << "PalPeek capture target updated: PID " << pid
                      << ", HWND " << raw_hwnd;
      return {{"ok", true}, {"generation", target->generation}};
    }

    if (command == "clearTarget") {
      {
        std::scoped_lock lock(target_mutex);
        target.reset();
        ++generation;
      }
      rtsp_stream::terminate_sessions();
      return {{"ok", true}};
    }

    if (command == "stopSessions") {
      rtsp_stream::terminate_sessions();
      return {{"ok", true}};
    }

    if (command == "pair") {
      const auto pin = request.value("pin", "");
      const auto client_id = request.value("clientId", "PalPeek");
      if (pin.size() != 4) {
        return {{"ok", false}, {"error", "Pairing PIN must contain four digits"}};
      }
      const bool accepted = nvhttp::pin(pin, client_id);
      return accepted
        ? nlohmann::json {{"ok", true}}
        : nlohmann::json {{"ok", false}, {"error", "No matching Moonlight pairing request"}};
    }

    if (command == "status") {
      auto current = palpeek::capture_target();
      if (!current) {
        return {{"ok", true}, {"target", nullptr}};
      }
      return {
        {"ok", true},
        {"target", {
          {"pid", current->root_pid},
          {"hwnd", reinterpret_cast<std::uintptr_t>(current->hwnd)},
          {"sessionId", current->session_id},
          {"generation", current->generation}
        }}
      };
    }

    return {{"ok", false}, {"error", "Unknown command"}};
  }

  void serve_pipe() {
    while (!stopping.load()) {
      HANDLE pipe = CreateNamedPipeA(
        pipe_name,
        PIPE_ACCESS_DUPLEX,
        PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS,
        1,
        4096,
        4096,
        0,
        nullptr
      );
      if (pipe == INVALID_HANDLE_VALUE) {
        BOOST_LOG(error) << "PalPeek failed to create its control pipe: " << GetLastError();
        return;
      }

      const bool connected = ConnectNamedPipe(pipe, nullptr) ||
                             GetLastError() == ERROR_PIPE_CONNECTED;
      if (connected && !stopping.load()) {
        std::string line;
        char buffer[1024];
        DWORD count = 0;
        while (ReadFile(pipe, buffer, sizeof(buffer), &count, nullptr) && count > 0) {
          line.append(buffer, count);
          if (line.find('\n') != std::string::npos || line.size() > 16384) {
            break;
          }
        }

        nlohmann::json response;
        try {
          response = line.size() > 16384
            ? nlohmann::json {{"ok", false}, {"error", "Command is too large"}}
            : handle_command(nlohmann::json::parse(line));
        } catch (const std::exception &error) {
          response = {{"ok", false}, {"error", error.what()}};
        }

        auto output = response.dump() + "\n";
        DWORD written = 0;
        WriteFile(pipe, output.data(), static_cast<DWORD>(output.size()), &written, nullptr);
        FlushFileBuffers(pipe);
        DisconnectNamedPipe(pipe);
      }
      CloseHandle(pipe);
    }
  }
}  // namespace

namespace palpeek {
  std::optional<capture_target_t> capture_target() {
    std::scoped_lock lock(target_mutex);
    if (!target || !IsWindow(target->hwnd) || !IsWindowVisible(target->hwnd)) {
      return std::nullopt;
    }
    return target;
  }

  std::thread start_control_pipe() {
    stopping.store(false);
    return std::thread {serve_pipe};
  }

  void stop_control_pipe() {
    stopping.store(true);
    HANDLE wake = CreateFileA(
      pipe_name,
      GENERIC_READ | GENERIC_WRITE,
      0,
      nullptr,
      OPEN_EXISTING,
      0,
      nullptr
    );
    if (wake != INVALID_HANDLE_VALUE) {
      CloseHandle(wake);
    }
  }
}  // namespace palpeek

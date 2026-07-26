/**
 * @file src/platform/windows/palpeek_ipc.cpp
 * @brief Local named-pipe bridge used by the PalPeek desktop application.
 */

#include "palpeek_ipc.h"

#include <algorithm>
#include <atomic>
#include <condition_variable>
#include <mutex>
#include <string>

#include <nlohmann/json.hpp>

#include "src/entry_handler.h"
#include "src/logging.h"
#include "src/nvhttp.h"
#include "src/rtsp.h"

namespace {
  constexpr auto pipe_name = R"(\\.\pipe\PalPeekCapture)";
  std::mutex state_mutex;
  std::condition_variable target_changed;
  std::optional<palpeek::capture_target_t> target;
  palpeek::capture_state_t capture_state {palpeek::capture_state_t::idle};
  palpeek::audio_state_t audio_state {palpeek::audio_state_t::idle};
  palpeek::encoding_state_t encoding_state {palpeek::encoding_state_t::waiting_for_target};
  struct runtime_error_t {
    std::string code;
    std::string message;
  };
  runtime_error_t capture_error;
  runtime_error_t audio_error;
  runtime_error_t encoding_error;
  std::atomic_uint64_t generation {0};
  std::atomic_bool stopping {false};

  nlohmann::json error_response(std::string_view code, std::string_view message) {
    return {
      {"ok", false},
      {"error", {
        {"code", code},
        {"message", message}
      }}
    };
  }

  void set_error_locked(runtime_error_t &error, std::string_view error_code, std::string_view message) {
    if (!error_code.empty()) {
      error.code = error_code;
      error.message = message;
    } else {
      error.code.clear();
      error.message.clear();
    }
  }

  const char *to_string(palpeek::capture_state_t state) {
    switch (state) {
      case palpeek::capture_state_t::idle:
        return "idle";
      case palpeek::capture_state_t::target_ready:
        return "targetReady";
      case palpeek::capture_state_t::capturing:
        return "capturing";
      case palpeek::capture_state_t::error:
        return "error";
    }
    return "error";
  }

  const char *to_string(palpeek::audio_state_t state) {
    switch (state) {
      case palpeek::audio_state_t::idle:
        return "idle";
      case palpeek::audio_state_t::ready:
        return "ready";
      case palpeek::audio_state_t::capturing:
        return "capturing";
      case palpeek::audio_state_t::error:
        return "error";
    }
    return "error";
  }

  const char *to_string(palpeek::encoding_state_t state) {
    switch (state) {
      case palpeek::encoding_state_t::waiting_for_target:
        return "waitingForTarget";
      case palpeek::encoding_state_t::probing:
        return "probing";
      case palpeek::encoding_state_t::ready:
        return "ready";
      case palpeek::encoding_state_t::error:
        return "error";
    }
    return "error";
  }

  void clear_target_locked() {
    target.reset();
    ++generation;
    capture_state = palpeek::capture_state_t::idle;
    audio_state = palpeek::audio_state_t::idle;
    encoding_state = palpeek::encoding_state_t::waiting_for_target;
    capture_error = {};
    audio_error = {};
    encoding_error = {};
    target_changed.notify_all();
  }

  nlohmann::json status_response() {
    const bool streaming = rtsp_stream::session_count() > 0;
    std::scoped_lock lock(state_mutex);
    const auto &active_error = !capture_error.code.empty()
      ? capture_error
      : (!audio_error.code.empty() ? audio_error : encoding_error);
    nlohmann::json response {
      {"ok", true},
      {"protocolVersion", palpeek::protocol_version},
      {"capture", to_string(capture_state)},
      {"audio", to_string(audio_state)},
      {"encoding", streaming ? "streaming" : to_string(encoding_state)},
      {"target", nullptr},
      {"errorCode", active_error.code.empty() ? nlohmann::json(nullptr) : nlohmann::json(active_error.code)},
      {"message", active_error.message.empty() ? nlohmann::json(nullptr) : nlohmann::json(active_error.message)}
    };
    if (target) {
      response["target"] = {
        {"pid", target->root_pid},
        {"hwnd", reinterpret_cast<std::uintptr_t>(target->hwnd)},
        {"sessionId", target->session_id},
        {"generation", target->generation}
      };
    }
    return response;
  }

  nlohmann::json handle_command(const nlohmann::json &request) {
    const auto request_version = request.value("protocolVersion", 0);
    if (request_version != palpeek::protocol_version) {
      return error_response("protocol_version_mismatch", "Unsupported PalPeek IPC protocol version");
    }

    const auto command = request.value("command", "");
    if (command == "setTarget") {
      const auto pid = request.value("pid", 0u);
      const auto raw_hwnd = request.value("hwnd", std::uint64_t {0});
      auto hwnd = reinterpret_cast<HWND>(static_cast<std::uintptr_t>(raw_hwnd));
      DWORD window_pid = 0;
      GetWindowThreadProcessId(hwnd, &window_pid);
      if (!pid || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || window_pid != pid) {
        return error_response("invalid_window", "The selected HWND is not a visible window owned by the target PID");
      }

      std::scoped_lock lock(state_mutex);
      if (target &&
          target->root_pid == pid &&
          target->hwnd == hwnd &&
          target->session_id == request.value("sessionId", "")) {
        return {{"ok", true}, {"generation", target->generation}};
      }

      target = palpeek::capture_target_t {
        pid,
        hwnd,
        request.value("appId", ""),
        request.value("name", ""),
        request.value("sessionId", ""),
        ++generation
      };
      capture_state = palpeek::capture_state_t::target_ready;
      audio_state = palpeek::audio_state_t::idle;
      encoding_state = palpeek::encoding_state_t::waiting_for_target;
      capture_error = {};
      audio_error = {};
      encoding_error = {};
      target_changed.notify_all();
      BOOST_LOG(info) << "PalPeek capture target updated: PID " << pid
                      << ", HWND " << raw_hwnd;
      return {{"ok", true}, {"generation", target->generation}};
    }

    if (command == "clearTarget") {
      {
        std::scoped_lock lock(state_mutex);
        clear_target_locked();
      }
      rtsp_stream::terminate_sessions();
      return {{"ok", true}};
    }

    if (command == "stopSessions") {
      rtsp_stream::terminate_sessions();
      return {{"ok", true}};
    }

    if (command == "sessionEnded") {
      {
        std::scoped_lock lock(state_mutex);
        const auto session_id = request.value("sessionId", "");
        if (target && target->session_id != session_id) {
          return error_response("stale_session", "The capture target belongs to a different session");
        }
        clear_target_locked();
      }
      rtsp_stream::terminate_sessions();
      return {{"ok", true}};
    }

    if (command == "pair") {
      const auto pin = request.value("pin", "");
      const auto client_id = request.value("clientId", "PalPeek");
      const auto client_address = request.value("clientAddress", "");
      if (pin.size() != 4 ||
          !std::all_of(pin.begin(), pin.end(), [](unsigned char value) {
            return value >= '0' && value <= '9';
          })) {
        return error_response("invalid_pin", "Pairing PIN must contain four digits");
      }
      const bool accepted = nvhttp::pin(pin, client_id, client_address);
      return accepted
        ? nlohmann::json {{"ok", true}}
        : error_response("pairing_rejected", "No matching Moonlight pairing request");
    }

    if (command == "status") {
      return status_response();
    }

    if (command == "shutdown") {
      std::thread([]() {
        std::this_thread::sleep_for(std::chrono::milliseconds(50));
        lifetime::exit_sunshine(0, true);
      }).detach();
      return {{"ok", true}};
    }

    return error_response("unknown_command", "Unknown PalPeek IPC command");
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
            ? error_response("command_too_large", "Command is too large")
            : handle_command(nlohmann::json::parse(line));
        } catch (const nlohmann::json::exception &error) {
          response = error_response("invalid_json", error.what());
        } catch (const std::exception &error) {
          response = error_response("internal_error", error.what());
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
    std::scoped_lock lock(state_mutex);
    if (!target) {
      return std::nullopt;
    }
    if (!IsWindow(target->hwnd) || !IsWindowVisible(target->hwnd)) {
      target.reset();
      ++generation;
      capture_state = capture_state_t::error;
      audio_state = audio_state_t::idle;
      encoding_state = encoding_state_t::error;
      set_error_locked(capture_error, "window_unavailable", "The selected game window no longer exists");
      target_changed.notify_all();
      return std::nullopt;
    }
    return target;
  }

  bool wait_for_capture_target(std::chrono::milliseconds timeout) {
    std::unique_lock lock(state_mutex);
    return target_changed.wait_for(lock, timeout, []() {
      return stopping.load() || target.has_value();
    }) && target.has_value();
  }

  void set_capture_state(capture_state_t state, std::string_view error_code, std::string_view message) {
    std::scoped_lock lock(state_mutex);
    capture_state = state;
    set_error_locked(capture_error, error_code, message);
  }

  void set_audio_state(audio_state_t state, std::string_view error_code, std::string_view message) {
    std::scoped_lock lock(state_mutex);
    audio_state = state;
    set_error_locked(audio_error, error_code, message);
  }

  void set_encoding_state(encoding_state_t state, std::string_view error_code, std::string_view message) {
    std::scoped_lock lock(state_mutex);
    encoding_state = state;
    set_error_locked(encoding_error, error_code, message);
  }

  std::thread start_control_pipe() {
    stopping.store(false);
    return std::thread {serve_pipe};
  }

  void stop_control_pipe() {
    stopping.store(true);
    target_changed.notify_all();
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

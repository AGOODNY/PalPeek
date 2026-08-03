/**
 * @file src/platform/windows/palpeek_web_stream.h
 * @brief Browser-compatible H.264/AAC fragmented MP4 output for PalPeek.
 */
#pragma once

#include <string>
#include <string_view>

namespace palpeek::web_stream {
  enum class state_t {
    stopped,
    starting,
    streaming,
    error,
  };

  bool start(
    std::string session_id,
    std::string_view quality,
    std::string_view media_pipe,
    std::string &error
  );
  bool stop(std::string_view session_id, std::string &error);
  void stop_any();
  state_t state();
  std::string error();
  const char *to_string(state_t state);
}  // namespace palpeek::web_stream

/**
 * @file src/platform/windows/palpeek_web_stream.cpp
 * @brief Browser-compatible H.264/AAC fragmented MP4 output for PalPeek.
 */

#include "palpeek_web_stream.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <future>
#include <memory>
#include <mutex>
#include <optional>
#include <stdexcept>
#include <string>
#include <thread>
#include <vector>

#include <windows.h>
#include <mfapi.h>
#include <mferror.h>
#include <mftransform.h>

#include "src/audio.h"
#include "src/globals.h"
#include "src/logging.h"
#include "src/video.h"

using namespace std::chrono_literals;

namespace {
  constexpr std::uint32_t media_magic = 0x4D575050;
  constexpr std::uint16_t media_version = 1;
  constexpr std::size_t max_media_payload = 8 * 1024 * 1024;
  constexpr int audio_sample_rate = 48000;
  constexpr int audio_channels = 2;
  constexpr int audio_frame_samples = 1024;
  constexpr std::uint32_t audio_bytes_per_second = 16000;

  template<class T>
  class com_ptr_t {
  public:
    com_ptr_t() = default;
    ~com_ptr_t() { reset(); }
    com_ptr_t(const com_ptr_t &) = delete;
    com_ptr_t &operator=(const com_ptr_t &) = delete;

    T *get() const { return value_; }
    T *operator->() const { return value_; }
    T **put() {
      reset();
      return &value_;
    }
    void reset() {
      if (value_) {
        value_->Release();
        value_ = nullptr;
      }
    }

  private:
    T *value_ {nullptr};
  };

  void check_hresult(HRESULT result, std::string_view operation) {
    if (FAILED(result)) {
      throw std::runtime_error(
        std::string {operation} + " failed (HRESULT " +
        std::to_string(static_cast<unsigned long>(result)) + ")");
    }
  }

  class bytes_t {
  public:
    std::vector<std::uint8_t> data;

    std::size_t box(std::string_view name) {
      auto offset = data.size();
      u32(0);
      fourcc(name);
      return offset;
    }

    void end_box(std::size_t offset) {
      patch_u32(offset, static_cast<std::uint32_t>(data.size() - offset));
    }

    void u8(std::uint8_t value) { data.push_back(value); }
    void u16(std::uint16_t value) {
      data.push_back(static_cast<std::uint8_t>(value >> 8));
      data.push_back(static_cast<std::uint8_t>(value));
    }
    void u24(std::uint32_t value) {
      data.push_back(static_cast<std::uint8_t>(value >> 16));
      data.push_back(static_cast<std::uint8_t>(value >> 8));
      data.push_back(static_cast<std::uint8_t>(value));
    }
    void u32(std::uint32_t value) {
      data.push_back(static_cast<std::uint8_t>(value >> 24));
      data.push_back(static_cast<std::uint8_t>(value >> 16));
      data.push_back(static_cast<std::uint8_t>(value >> 8));
      data.push_back(static_cast<std::uint8_t>(value));
    }
    void u64(std::uint64_t value) {
      u32(static_cast<std::uint32_t>(value >> 32));
      u32(static_cast<std::uint32_t>(value));
    }
    void zeros(std::size_t count) { data.insert(data.end(), count, 0); }
    void fourcc(std::string_view value) {
      for (std::size_t index = 0; index < 4; ++index) {
        data.push_back(index < value.size() ? static_cast<std::uint8_t>(value[index]) : 0);
      }
    }
    void append(const std::uint8_t *value, std::size_t length) {
      data.insert(data.end(), value, value + length);
    }
    void append(const std::vector<std::uint8_t> &value) {
      data.insert(data.end(), value.begin(), value.end());
    }
    void patch_u32(std::size_t offset, std::uint32_t value) {
      data[offset] = static_cast<std::uint8_t>(value >> 24);
      data[offset + 1] = static_cast<std::uint8_t>(value >> 16);
      data[offset + 2] = static_cast<std::uint8_t>(value >> 8);
      data[offset + 3] = static_cast<std::uint8_t>(value);
    }
  };

  void full_box(bytes_t &out, std::uint8_t version, std::uint32_t flags) {
    out.u8(version);
    out.u24(flags);
  }

  void matrix(bytes_t &out) {
    out.u32(0x00010000); out.u32(0); out.u32(0);
    out.u32(0); out.u32(0x00010000); out.u32(0);
    out.u32(0); out.u32(0); out.u32(0x40000000);
  }

  void empty_table(bytes_t &out, std::string_view name) {
    auto box = out.box(name);
    full_box(out, 0, 0);
    out.u32(0);
    out.end_box(box);
  }

  void data_information(bytes_t &out) {
    auto dinf = out.box("dinf");
    auto dref = out.box("dref");
    full_box(out, 0, 0);
    out.u32(1);
    auto url = out.box("url ");
    full_box(out, 0, 1);
    out.end_box(url);
    out.end_box(dref);
    out.end_box(dinf);
  }

  void descriptor_length(bytes_t &out, std::size_t length) {
    std::array<std::uint8_t, 4> encoded {};
    int count = 0;
    do {
      encoded[count++] = static_cast<std::uint8_t>(length & 0x7f);
      length >>= 7;
    } while (length && count < 4);
    for (int index = count - 1; index >= 0; --index) {
      out.u8(encoded[index] | (index ? 0x80 : 0));
    }
  }

  struct nal_units_t {
    std::vector<std::uint8_t> sample;
    std::vector<std::uint8_t> sps;
    std::vector<std::uint8_t> pps;
  };

  std::optional<std::size_t> start_code(const std::uint8_t *data, std::size_t size, std::size_t from, std::size_t &length) {
    for (auto index = from; index + 3 <= size; ++index) {
      if (data[index] == 0 && data[index + 1] == 0 && data[index + 2] == 1) {
        length = 3;
        return index;
      }
      if (index + 4 <= size && data[index] == 0 && data[index + 1] == 0 &&
          data[index + 2] == 0 && data[index + 3] == 1) {
        length = 4;
        return index;
      }
    }
    return std::nullopt;
  }

  nal_units_t convert_annex_b(const std::uint8_t *data, std::size_t size) {
    nal_units_t result;
    std::size_t code_length = 0;
    auto current = start_code(data, size, 0, code_length);
    if (!current) {
      return result;
    }
    while (current) {
      auto payload_start = *current + code_length;
      std::size_t next_length = 0;
      auto next = start_code(data, size, payload_start, next_length);
      auto payload_end = next.value_or(size);
      while (payload_end > payload_start && data[payload_end - 1] == 0) {
        --payload_end;
      }
      if (payload_end > payload_start) {
        auto type = data[payload_start] & 0x1f;
        if (type == 7) {
          result.sps.assign(data + payload_start, data + payload_end);
        } else if (type == 8) {
          result.pps.assign(data + payload_start, data + payload_end);
        } else if (type != 9) {
          auto length = static_cast<std::uint32_t>(payload_end - payload_start);
          result.sample.push_back(static_cast<std::uint8_t>(length >> 24));
          result.sample.push_back(static_cast<std::uint8_t>(length >> 16));
          result.sample.push_back(static_cast<std::uint8_t>(length >> 8));
          result.sample.push_back(static_cast<std::uint8_t>(length));
          result.sample.insert(result.sample.end(), data + payload_start, data + payload_end);
        }
      }
      current = next;
      code_length = next_length;
    }
    return result;
  }

  std::vector<std::uint8_t> apply_replacements(video::packet_raw_t &packet) {
    std::vector<std::uint8_t> payload(packet.data(), packet.data() + packet.data_size());
    if (!packet.is_idr() || !packet.replacements) {
      return payload;
    }
    for (const auto &replacement : *packet.replacements) {
      auto begin = std::search(
        payload.begin(), payload.end(),
        replacement.old.begin(), replacement.old.end());
      if (begin == payload.end()) {
        continue;
      }
      auto offset = static_cast<std::size_t>(std::distance(payload.begin(), begin));
      payload.erase(begin, begin + replacement.old.size());
      payload.insert(payload.begin() + offset, replacement._new.begin(), replacement._new.end());
    }
    return payload;
  }

  class pipe_writer_t {
  public:
    ~pipe_writer_t() {
      if (handle_ != INVALID_HANDLE_VALUE) {
        CloseHandle(handle_);
      }
    }

    bool connect(std::string_view name, std::string &error) {
      auto path = std::string {R"(\\.\pipe\)"} + std::string {name};
      if (!WaitNamedPipeA(path.c_str(), 5000) && GetLastError() != ERROR_SEM_TIMEOUT) {
        error = "PalPeek web media pipe is unavailable";
        return false;
      }
      handle_ = CreateFileA(path.c_str(), GENERIC_WRITE, 0, nullptr, OPEN_EXISTING, 0, nullptr);
      if (handle_ == INVALID_HANDLE_VALUE) {
        error = "Unable to connect to PalPeek web media pipe";
        return false;
      }
      return true;
    }

    bool send(
      std::uint8_t type,
      std::string_view session_id,
      std::int64_t sequence,
      std::int32_t duration_ms,
      const std::vector<std::uint8_t> &payload
    ) {
      if (handle_ == INVALID_HANDLE_VALUE || session_id.empty() || session_id.size() > 128 ||
          payload.size() > max_media_payload) {
        return false;
      }
      std::array<std::uint8_t, 26> header {};
      auto little16 = [&](std::size_t offset, std::uint16_t value) {
        header[offset] = static_cast<std::uint8_t>(value);
        header[offset + 1] = static_cast<std::uint8_t>(value >> 8);
      };
      auto little32 = [&](std::size_t offset, std::uint32_t value) {
        for (int index = 0; index < 4; ++index) header[offset + index] = static_cast<std::uint8_t>(value >> (index * 8));
      };
      auto little64 = [&](std::size_t offset, std::uint64_t value) {
        for (int index = 0; index < 8; ++index) header[offset + index] = static_cast<std::uint8_t>(value >> (index * 8));
      };
      little32(0, media_magic);
      little16(4, media_version);
      header[6] = type;
      little64(8, static_cast<std::uint64_t>(sequence));
      little32(16, static_cast<std::uint32_t>(duration_ms));
      little16(20, static_cast<std::uint16_t>(session_id.size()));
      little32(22, static_cast<std::uint32_t>(payload.size()));
      return write_all(header.data(), header.size()) &&
             write_all(reinterpret_cast<const std::uint8_t *>(session_id.data()), session_id.size()) &&
             write_all(payload.data(), payload.size());
    }

  private:
    bool write_all(const std::uint8_t *data, std::size_t size) {
      std::size_t offset = 0;
      while (offset < size) {
        DWORD written = 0;
        if (!WriteFile(handle_, data + offset, static_cast<DWORD>(size - offset), &written, nullptr) || written == 0) {
          return false;
        }
        offset += written;
      }
      return true;
    }

    HANDLE handle_ {INVALID_HANDLE_VALUE};
  };

  struct sample_t {
    std::vector<std::uint8_t> data;
    std::uint32_t duration;
    bool key;
  };

  class fragment_muxer_t {
  public:
    fragment_muxer_t(std::string session_id, int fps, pipe_writer_t &pipe):
        session_id_ {std::move(session_id)},
        fps_ {fps},
        pipe_ {pipe} {
    }

    void video(video::packet_raw_t &packet) {
      auto payload = apply_replacements(packet);
      auto nals = convert_annex_b(payload.data(), payload.size());
      if (!nals.sps.empty()) sps_ = std::move(nals.sps);
      if (!nals.pps.empty()) pps_ = std::move(nals.pps);
      if (nals.sample.empty()) return;

      std::scoped_lock lock(mutex_);
      if (packet.is_idr() && !video_.empty()) {
        flush_locked();
      }
      if (video_.empty() && !packet.is_idr()) {
        return;
      }
      video_.push_back(sample_t {
        std::move(nals.sample),
        static_cast<std::uint32_t>(90000 / fps_),
        packet.is_idr()
      });
      emit_init_locked();
    }

    void audio(std::vector<std::uint8_t> packet, std::uint32_t duration) {
      std::scoped_lock lock(mutex_);
      // Align the audio timeline to the first decodable video keyframe.
      if (video_.empty()) return;
      audio_.push_back(sample_t {std::move(packet), duration, true});
    }

    void finish() {
      std::scoped_lock lock(mutex_);
      if (!video_.empty()) flush_locked();
    }

  private:
    void emit_init_locked() {
      if (init_sent_ || sps_.size() < 4 || pps_.empty()) return;
      auto init = make_init();
      init_sent_ = pipe_.send(1, session_id_, 0, 0, init);
    }

    std::vector<std::uint8_t> make_init() {
      bytes_t out;
      auto ftyp = out.box("ftyp");
      out.fourcc("iso6"); out.u32(1);
      out.fourcc("iso6"); out.fourcc("mp41"); out.fourcc("avc1"); out.fourcc("dash");
      out.end_box(ftyp);
      auto moov = out.box("moov");
      movie_header(out);
      video_track(out);
      audio_track(out);
      auto mvex = out.box("mvex");
      track_extends(out, 1, static_cast<std::uint32_t>(90000 / fps_));
      track_extends(out, 2, 1024);
      out.end_box(mvex);
      out.end_box(moov);
      return std::move(out.data);
    }

    void movie_header(bytes_t &out) {
      auto box = out.box("mvhd");
      full_box(out, 0, 0); out.u32(0); out.u32(0); out.u32(1000); out.u32(0);
      out.u32(0x00010000); out.u16(0x0100); out.u16(0); out.zeros(8); matrix(out);
      out.zeros(24); out.u32(3); out.end_box(box);
    }

    void track_header(bytes_t &out, std::uint32_t id, bool audio) {
      auto box = out.box("tkhd");
      full_box(out, 0, 7); out.u32(0); out.u32(0); out.u32(id); out.u32(0); out.u32(0);
      out.zeros(8); out.u16(0); out.u16(0); out.u16(audio ? 0x0100 : 0); out.u16(0); matrix(out);
      out.u32(audio ? 0 : 1280u << 16); out.u32(audio ? 0 : 720u << 16); out.end_box(box);
    }

    void media_header(bytes_t &out, std::uint32_t timescale) {
      auto box = out.box("mdhd");
      full_box(out, 0, 0); out.u32(0); out.u32(0); out.u32(timescale); out.u32(0);
      out.u16(0x55c4); out.u16(0); out.end_box(box);
    }

    void handler(bytes_t &out, std::string_view type, std::string_view name) {
      auto box = out.box("hdlr");
      full_box(out, 0, 0); out.u32(0); out.fourcc(type); out.zeros(12);
      out.append(reinterpret_cast<const std::uint8_t *>(name.data()), name.size()); out.u8(0);
      out.end_box(box);
    }

    void sample_tables(bytes_t &out, bool audio) {
      auto stbl = out.box("stbl");
      auto stsd = out.box("stsd");
      full_box(out, 0, 0); out.u32(1);
      if (audio) audio_sample_entry(out); else video_sample_entry(out);
      out.end_box(stsd);
      empty_table(out, "stts"); empty_table(out, "stsc");
      auto stsz = out.box("stsz"); full_box(out, 0, 0); out.u32(0); out.u32(0); out.end_box(stsz);
      empty_table(out, "stco"); out.end_box(stbl);
    }

    void video_sample_entry(bytes_t &out) {
      auto avc1 = out.box("avc1"); out.zeros(6); out.u16(1); out.zeros(16);
      out.u16(1280); out.u16(720); out.u32(0x00480000); out.u32(0x00480000); out.u32(0);
      out.u16(1); out.zeros(32); out.u16(24); out.u16(0xffff);
      auto avcc = out.box("avcC");
      out.u8(1); out.u8(sps_[1]); out.u8(sps_[2]); out.u8(sps_[3]); out.u8(0xff); out.u8(0xe1);
      out.u16(static_cast<std::uint16_t>(sps_.size())); out.append(sps_);
      out.u8(1); out.u16(static_cast<std::uint16_t>(pps_.size())); out.append(pps_);
      out.end_box(avcc); out.end_box(avc1);
    }

    void audio_sample_entry(bytes_t &out) {
      auto mp4a = out.box("mp4a"); out.zeros(6); out.u16(1); out.zeros(8);
      out.u16(2); out.u16(16); out.u16(0); out.u16(0); out.u32(audio_sample_rate << 16);
      auto esds = out.box("esds"); full_box(out, 0, 0);
      bytes_t decoder;
      decoder.u8(0x04); descriptor_length(decoder, 17);
      decoder.u8(0x40); decoder.u8(0x15); decoder.u24(0); decoder.u32(128000); decoder.u32(128000);
      decoder.u8(0x05); descriptor_length(decoder, 2); decoder.u8(0x11); decoder.u8(0x90);
      bytes_t es;
      es.u16(2); es.u8(0); es.append(decoder.data); es.u8(0x06); es.u8(1); es.u8(2);
      out.u8(0x03); descriptor_length(out, es.data.size()); out.append(es.data);
      out.end_box(esds); out.end_box(mp4a);
    }

    void video_track(bytes_t &out) {
      auto trak = out.box("trak"); track_header(out, 1, false);
      auto mdia = out.box("mdia"); media_header(out, 90000); handler(out, "vide", "VideoHandler");
      auto minf = out.box("minf"); auto vmhd = out.box("vmhd"); full_box(out, 0, 1); out.zeros(8); out.end_box(vmhd);
      data_information(out); sample_tables(out, false); out.end_box(minf); out.end_box(mdia); out.end_box(trak);
    }

    void audio_track(bytes_t &out) {
      auto trak = out.box("trak"); track_header(out, 2, true);
      auto mdia = out.box("mdia"); media_header(out, audio_sample_rate); handler(out, "soun", "SoundHandler");
      auto minf = out.box("minf"); auto smhd = out.box("smhd"); full_box(out, 0, 0); out.u16(0); out.u16(0); out.end_box(smhd);
      data_information(out); sample_tables(out, true); out.end_box(minf); out.end_box(mdia); out.end_box(trak);
    }

    void track_extends(bytes_t &out, std::uint32_t id, std::uint32_t duration) {
      auto box = out.box("trex"); full_box(out, 0, 0); out.u32(id); out.u32(1); out.u32(duration);
      out.u32(0); out.u32(id == 1 ? 0x01010000 : 0); out.end_box(box);
    }

    void flush_locked() {
      emit_init_locked();
      if (!init_sent_ || video_.empty()) {
        video_.clear(); audio_.clear();
        return;
      }
      auto segment = make_fragment();
      auto video_duration = std::uint64_t {0};
      for (const auto &sample : video_) video_duration += sample.duration;
      auto duration_ms = static_cast<std::int32_t>((video_duration * 1000 + 89999) / 90000);
      if (!pipe_.send(2, session_id_, sequence_++, duration_ms, segment)) {
        video_decode_time_ += video_duration;
        for (const auto &sample : audio_) audio_decode_time_ += sample.duration;
        video_.clear(); audio_.clear();
        return;
      }
      video_decode_time_ += video_duration;
      for (const auto &sample : audio_) audio_decode_time_ += sample.duration;
      video_.clear(); audio_.clear();
    }

    std::vector<std::uint8_t> make_fragment() {
      bytes_t out;
      auto moof = out.box("moof");
      auto mfhd = out.box("mfhd"); full_box(out, 0, 0); out.u32(static_cast<std::uint32_t>(sequence_ + 1)); out.end_box(mfhd);
      std::size_t video_offset = 0;
      sample_traf(out, 1, video_decode_time_, video_, true, video_offset);
      std::size_t audio_offset = 0;
      sample_traf(out, 2, audio_decode_time_, audio_, false, audio_offset);
      out.end_box(moof);
      auto mdat_offset = out.data.size();
      auto mdat = out.box("mdat");
      std::size_t video_bytes = 0;
      for (const auto &sample : video_) { out.append(sample.data); video_bytes += sample.data.size(); }
      for (const auto &sample : audio_) out.append(sample.data);
      out.end_box(mdat);
      out.patch_u32(video_offset, static_cast<std::uint32_t>(mdat_offset + 8));
      out.patch_u32(audio_offset, static_cast<std::uint32_t>(mdat_offset + 8 + video_bytes));
      return std::move(out.data);
    }

    void sample_traf(
      bytes_t &out,
      std::uint32_t id,
      std::uint64_t decode_time,
      const std::vector<sample_t> &samples,
      bool video,
      std::size_t &data_offset
    ) {
      auto traf = out.box("traf");
      auto tfhd = out.box("tfhd"); full_box(out, 0, 0x020000); out.u32(id); out.end_box(tfhd);
      auto tfdt = out.box("tfdt"); full_box(out, 1, 0); out.u64(decode_time); out.end_box(tfdt);
      auto trun = out.box("trun"); full_box(out, 0, video ? 0x000701 : 0x000301);
      out.u32(static_cast<std::uint32_t>(samples.size())); data_offset = out.data.size(); out.u32(0);
      for (const auto &sample : samples) {
        out.u32(sample.duration); out.u32(static_cast<std::uint32_t>(sample.data.size()));
        if (video) out.u32(sample.key ? 0x02000000 : 0x01010000);
      }
      out.end_box(trun); out.end_box(traf);
    }

    std::mutex mutex_;
    std::string session_id_;
    int fps_;
    pipe_writer_t &pipe_;
    std::vector<std::uint8_t> sps_;
    std::vector<std::uint8_t> pps_;
    std::vector<sample_t> video_;
    std::vector<sample_t> audio_;
    bool init_sent_ {false};
    std::int64_t sequence_ {0};
    std::uint64_t video_decode_time_ {0};
    std::uint64_t audio_decode_time_ {0};
  };

  class aac_encoder_t {
  public:
    explicit aac_encoder_t(fragment_muxer_t &muxer): muxer_ {muxer} {
      try {
        initialize();
      } catch (...) {
        shutdown();
        throw;
      }
    }

    ~aac_encoder_t() { shutdown(); }

    void push(std::vector<float> &&samples) {
      if (failed_) return;
      pending_.insert(pending_.end(), samples.begin(), samples.end());
      constexpr auto required = static_cast<std::size_t>(audio_frame_samples * audio_channels);
      while (pending_.size() >= required) {
        try {
          encode_frame(pending_.data());
        } catch (const std::exception &exception) {
          failed_ = true;
          BOOST_LOG(::error) << "PalPeek AAC encoder stopped: " << exception.what();
          return;
        }
        pending_.erase(pending_.begin(), pending_.begin() + required);
      }
    }

  private:
    void initialize() {
      auto result = CoInitializeEx(nullptr, COINIT_MULTITHREADED | COINIT_SPEED_OVER_MEMORY);
      if (FAILED(result) && result != RPC_E_CHANGED_MODE) {
        check_hresult(result, "CoInitializeEx");
      }
      com_initialized_ = SUCCEEDED(result);

      check_hresult(MFStartup(MF_VERSION, MFSTARTUP_LITE), "MFStartup");
      media_foundation_started_ = true;

      // CLSID_AACMFTEncoder is documented in wmcodecdsp.h but is not exported
      // by every MinGW import library, so keep the documented identifier local.
      static constexpr GUID aac_encoder_clsid {
        0x93af0c51, 0x2275, 0x45d2, {0xa3, 0x5b, 0xf2, 0xba, 0x21, 0xca, 0xed, 0x00}
      };
      check_hresult(CoCreateInstance(
        aac_encoder_clsid,
        nullptr,
        CLSCTX_INPROC_SERVER,
        IID_IMFTransform,
        reinterpret_cast<void **>(transform_.put())), "Create Media Foundation AAC encoder");

      auto stream_result = transform_->GetStreamIDs(1, &input_stream_id_, 1, &output_stream_id_);
      if (stream_result == E_NOTIMPL) {
        input_stream_id_ = output_stream_id_ = 0;
      } else {
        check_hresult(stream_result, "Get AAC stream identifiers");
      }

      com_ptr_t<IMFMediaType> output_type;
      check_hresult(MFCreateMediaType(output_type.put()), "Create AAC output type");
      check_hresult(output_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio), "Set AAC major type");
      check_hresult(output_type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_AAC), "Set AAC subtype");
      check_hresult(output_type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16), "Set AAC bit depth");
      check_hresult(output_type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, audio_sample_rate), "Set AAC sample rate");
      check_hresult(output_type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, audio_channels), "Set AAC channels");
      check_hresult(output_type->SetUINT32(MF_MT_AUDIO_AVG_BYTES_PER_SECOND, audio_bytes_per_second), "Set AAC bitrate");
      check_hresult(output_type->SetUINT32(MF_MT_AAC_PAYLOAD_TYPE, 0), "Set AAC payload type");
      check_hresult(output_type->SetUINT32(MF_MT_AAC_AUDIO_PROFILE_LEVEL_INDICATION, 0x29), "Set AAC profile");
      check_hresult(transform_->SetOutputType(output_stream_id_, output_type.get(), 0), "Configure AAC output");

      com_ptr_t<IMFMediaType> input_type;
      check_hresult(MFCreateMediaType(input_type.put()), "Create PCM input type");
      check_hresult(input_type->SetGUID(MF_MT_MAJOR_TYPE, MFMediaType_Audio), "Set PCM major type");
      check_hresult(input_type->SetGUID(MF_MT_SUBTYPE, MFAudioFormat_PCM), "Set PCM subtype");
      check_hresult(input_type->SetUINT32(MF_MT_AUDIO_BITS_PER_SAMPLE, 16), "Set PCM bit depth");
      check_hresult(input_type->SetUINT32(MF_MT_AUDIO_SAMPLES_PER_SECOND, audio_sample_rate), "Set PCM sample rate");
      check_hresult(input_type->SetUINT32(MF_MT_AUDIO_NUM_CHANNELS, audio_channels), "Set PCM channels");
      check_hresult(input_type->SetUINT32(MF_MT_AUDIO_BLOCK_ALIGNMENT, audio_channels * sizeof(std::int16_t)), "Set PCM alignment");
      check_hresult(input_type->SetUINT32(
        MF_MT_AUDIO_AVG_BYTES_PER_SECOND,
        audio_sample_rate * audio_channels * sizeof(std::int16_t)), "Set PCM byte rate");
      check_hresult(input_type->SetUINT32(MF_MT_ALL_SAMPLES_INDEPENDENT, TRUE), "Set PCM independence");
      check_hresult(input_type->SetUINT32(MF_MT_FIXED_SIZE_SAMPLES, TRUE), "Set PCM sample size mode");
      check_hresult(transform_->SetInputType(input_stream_id_, input_type.get(), 0), "Configure PCM input");

      check_hresult(transform_->GetOutputStreamInfo(output_stream_id_, &output_info_), "Get AAC output information");
      check_hresult(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, 0), "Begin AAC streaming");
      check_hresult(transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_START_OF_STREAM, 0), "Start AAC stream");
    }

    void shutdown() {
      if (transform_.get()) {
        transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_OF_STREAM, 0);
        transform_->ProcessMessage(MFT_MESSAGE_NOTIFY_END_STREAMING, 0);
      }
      transform_.reset();
      if (media_foundation_started_) {
        MFShutdown();
        media_foundation_started_ = false;
      }
      if (com_initialized_) {
        CoUninitialize();
        com_initialized_ = false;
      }
    }

    void encode_frame(const float *samples) {
      constexpr auto byte_count = audio_frame_samples * audio_channels * sizeof(std::int16_t);
      com_ptr_t<IMFMediaBuffer> input_buffer;
      check_hresult(MFCreateMemoryBuffer(byte_count, input_buffer.put()), "Allocate PCM buffer");
      BYTE *destination = nullptr;
      check_hresult(input_buffer->Lock(&destination, nullptr, nullptr), "Lock PCM buffer");
      auto pcm = reinterpret_cast<std::int16_t *>(destination);
      for (std::size_t index = 0; index < audio_frame_samples * audio_channels; ++index) {
        auto value = std::clamp(samples[index], -1.0f, 1.0f);
        pcm[index] = static_cast<std::int16_t>(std::lround(value * 32767.0f));
      }
      input_buffer->Unlock();
      check_hresult(input_buffer->SetCurrentLength(byte_count), "Commit PCM buffer");

      com_ptr_t<IMFSample> input_sample;
      check_hresult(MFCreateSample(input_sample.put()), "Create PCM sample");
      check_hresult(input_sample->AddBuffer(input_buffer.get()), "Attach PCM buffer");
      auto sample_time = next_sample_ * 10'000'000LL / audio_sample_rate;
      auto sample_duration = audio_frame_samples * 10'000'000LL / audio_sample_rate;
      check_hresult(input_sample->SetSampleTime(sample_time), "Set PCM timestamp");
      check_hresult(input_sample->SetSampleDuration(sample_duration), "Set PCM duration");

      auto result = transform_->ProcessInput(input_stream_id_, input_sample.get(), 0);
      if (result == MF_E_NOTACCEPTING) {
        drain_output();
        result = transform_->ProcessInput(input_stream_id_, input_sample.get(), 0);
      }
      check_hresult(result, "Encode PCM input");
      next_sample_ += audio_frame_samples;
      drain_output();
    }

    void drain_output() {
      while (true) {
        IMFSample *sample = nullptr;
        if (!(output_info_.dwFlags & MFT_OUTPUT_STREAM_PROVIDES_SAMPLES)) {
          check_hresult(MFCreateSample(&sample), "Create AAC sample");
          IMFMediaBuffer *buffer = nullptr;
          auto capacity = std::max<DWORD>(output_info_.cbSize, 64 * 1024);
          auto result = MFCreateMemoryBuffer(capacity, &buffer);
          if (SUCCEEDED(result)) result = sample->AddBuffer(buffer);
          if (buffer) buffer->Release();
          if (FAILED(result)) {
            sample->Release();
            check_hresult(result, "Allocate AAC output");
          }
        }

        MFT_OUTPUT_DATA_BUFFER output {};
        output.dwStreamID = output_stream_id_;
        output.pSample = sample;
        DWORD status = 0;
        auto result = transform_->ProcessOutput(0, 1, &output, &status);
        if (output.pEvents) output.pEvents->Release();
        if (result == MF_E_TRANSFORM_NEED_MORE_INPUT) {
          if (output.pSample) output.pSample->Release();
          return;
        }
        if (FAILED(result)) {
          if (output.pSample) output.pSample->Release();
          check_hresult(result, "Read AAC output");
        }

        com_ptr_t<IMFMediaBuffer> contiguous;
        check_hresult(output.pSample->ConvertToContiguousBuffer(contiguous.put()), "Read AAC sample");
        BYTE *payload = nullptr;
        DWORD length = 0;
        check_hresult(contiguous->Lock(&payload, nullptr, &length), "Lock AAC sample");
        std::vector<std::uint8_t> encoded(payload, payload + length);
        contiguous->Unlock();
        output.pSample->Release();
        if (!encoded.empty()) {
          muxer_.audio(std::move(encoded), audio_frame_samples);
        }
      }
    }

    fragment_muxer_t &muxer_;
    com_ptr_t<IMFTransform> transform_;
    MFT_OUTPUT_STREAM_INFO output_info_ {};
    DWORD input_stream_id_ {0};
    DWORD output_stream_id_ {0};
    bool com_initialized_ {false};
    bool media_foundation_started_ {false};
    bool failed_ {false};
    std::vector<float> pending_;
    std::int64_t next_sample_ {0};
  };

  struct stream_context_t {
    std::string session_id;
    int fps;
    safe::mail_t mail {std::make_shared<safe::mail_raw_t>()};
    video::packet_queue_t video_packets;
    pipe_writer_t pipe;
    std::unique_ptr<fragment_muxer_t> muxer;
    std::thread video_capture;
    std::thread audio_capture;
    std::thread video_output;

    stream_context_t(std::string value, int rate): session_id {std::move(value)}, fps {rate} {
      video_packets = mail->queue<video::packet_t>("palpeek_web_video");
    }
  };

  std::mutex manager_mutex;
  std::unique_ptr<stream_context_t> context;
  std::atomic<palpeek::web_stream::state_t> current_state {palpeek::web_stream::state_t::stopped};
  std::string current_error;

  video::config_t video_config(int fps) {
    return video::config_t {
      1280, 720, fps, fps * 100, fps == 60 ? 4000 : 2000,
      1, 1, 2, 0, 0, 0, 0
    };
  }

  audio::config_t audio_config() {
    audio::config_t config {};
    config.packetDuration = 5;
    config.channels = 2;
    config.mask = 3;
    return config;
  }

  void stop_locked() {
    if (!context) {
      current_state.store(palpeek::web_stream::state_t::stopped);
      return;
    }
    context->mail->event<bool>(mail::shutdown)->raise(true);
    context->video_packets->stop();
    if (context->video_capture.joinable()) context->video_capture.join();
    if (context->audio_capture.joinable()) context->audio_capture.join();
    if (context->video_output.joinable()) context->video_output.join();
    if (context->muxer) context->muxer->finish();
    context.reset();
    current_state.store(palpeek::web_stream::state_t::stopped);
    current_error.clear();
  }
}  // namespace

namespace palpeek::web_stream {
  bool start(
    std::string session_id,
    std::string_view quality,
    std::string_view media_pipe,
    std::string &error
  ) {
    std::scoped_lock lock(manager_mutex);
    if (context && context->session_id == session_id) return true;
    stop_locked();
    if (session_id.empty() || media_pipe != "PalPeekWebMedia" ||
        (quality != "P720_30" && quality != "P720_60")) {
      error = "Invalid web stream configuration";
      return false;
    }
    current_state.store(state_t::starting);
    try {
      auto fps = quality == "P720_60" ? 60 : 30;
      context = std::make_unique<stream_context_t>(session_id, fps);
      if (!context->pipe.connect(media_pipe, error)) {
        context.reset(); current_state.store(state_t::error); current_error = error; return false;
      }
      context->muxer = std::make_unique<fragment_muxer_t>(session_id, fps, context->pipe);
      auto raw = context.get();
      auto audio_ready = std::make_shared<std::promise<std::string>>();
      auto audio_result = audio_ready->get_future();
      raw->audio_capture = std::thread([raw, audio_ready]() {
        bool initialized = false;
        try {
          aac_encoder_t encoder {*raw->muxer};
          audio_ready->set_value({});
          initialized = true;
          audio::capture(raw->mail, audio_config(), raw, [&encoder](std::vector<float> &&samples) {
            encoder.push(std::move(samples));
          });
        } catch (const std::exception &exception) {
          if (!initialized) {
            audio_ready->set_value(exception.what());
          } else {
            BOOST_LOG(::error) << "PalPeek audio capture stopped: " << exception.what();
          }
        }
      });
      auto audio_error = audio_result.get();
      if (!audio_error.empty()) throw std::runtime_error(audio_error);
      raw->video_output = std::thread([raw]() {
        auto idr = raw->mail->event<bool>(mail::idr);
        int frames = 0;
        while (auto packet = raw->video_packets->pop()) {
          raw->muxer->video(*packet);
          if (++frames >= raw->fps) { frames = 0; idr->raise(true); }
        }
      });
      raw->video_capture = std::thread([raw]() {
        video::capture(raw->mail, video_config(raw->fps), raw, raw->video_packets);
      });
      current_state.store(state_t::streaming);
      current_error.clear();
      BOOST_LOG(info) << "PalPeek browser stream started for session " << session_id;
      return true;
    } catch (const std::exception &exception) {
      error = exception.what();
      stop_locked();
      current_error = error; current_state.store(state_t::error);
      return false;
    }
  }

  bool stop(std::string_view session_id, std::string &error) {
    std::scoped_lock lock(manager_mutex);
    if (!context) return true;
    if (context->session_id != session_id) { error = "The browser stream belongs to a different session"; return false; }
    stop_locked(); return true;
  }

  void stop_any() { std::scoped_lock lock(manager_mutex); stop_locked(); }
  state_t state() { return current_state.load(); }
  std::string error() { std::scoped_lock lock(manager_mutex); return current_error; }
  const char *to_string(state_t value) {
    switch (value) {
      case state_t::stopped: return "stopped";
      case state_t::starting: return "starting";
      case state_t::streaming: return "streaming";
      case state_t::error: return "error";
    }
    return "error";
  }
}  // namespace palpeek::web_stream

/**
 * @file src/platform/windows/palpeek_web_stream.cpp
 * @brief Browser-compatible H.264/AAC fragmented MP4 output for PalPeek.
 */

#include "palpeek_web_stream.h"

#include <algorithm>
#include <array>
#include <atomic>
#include <chrono>
#include <cstdint>
#include <cstring>
#include <memory>
#include <mutex>
#include <optional>
#include <string>
#include <thread>
#include <vector>

#include <windows.h>

extern "C" {
#include <libavcodec/avcodec.h>
#include <libavutil/channel_layout.h>
#include <libavutil/error.h>
#include <libavutil/frame.h>
}

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
      auto codec = avcodec_find_encoder(AV_CODEC_ID_AAC);
      if (!codec) throw std::runtime_error("AAC encoder is unavailable");
      context_ = avcodec_alloc_context3(codec);
      if (!context_) throw std::bad_alloc();
      context_->sample_rate = audio_sample_rate;
      context_->sample_fmt = AV_SAMPLE_FMT_FLTP;
      context_->bit_rate = 128000;
      context_->time_base = AVRational {1, audio_sample_rate};
      av_channel_layout_default(&context_->ch_layout, audio_channels);
      if (avcodec_open2(context_, codec, nullptr) < 0) throw std::runtime_error("Unable to initialize AAC encoder");
      frame_ = av_frame_alloc();
      if (!frame_) throw std::bad_alloc();
      frame_->format = context_->sample_fmt;
      frame_->sample_rate = context_->sample_rate;
      frame_->nb_samples = context_->frame_size;
      av_channel_layout_copy(&frame_->ch_layout, &context_->ch_layout);
      if (av_frame_get_buffer(frame_, 0) < 0) throw std::runtime_error("Unable to allocate AAC frame");
    }

    ~aac_encoder_t() {
      av_frame_free(&frame_);
      avcodec_free_context(&context_);
    }

    void push(std::vector<float> &&samples) {
      pending_.insert(pending_.end(), samples.begin(), samples.end());
      auto required = static_cast<std::size_t>(frame_->nb_samples * audio_channels);
      while (pending_.size() >= required) {
        av_frame_make_writable(frame_);
        auto left = reinterpret_cast<float *>(frame_->data[0]);
        auto right = reinterpret_cast<float *>(frame_->data[1]);
        for (int index = 0; index < frame_->nb_samples; ++index) {
          left[index] = pending_[index * 2]; right[index] = pending_[index * 2 + 1];
        }
        pending_.erase(pending_.begin(), pending_.begin() + required);
        frame_->pts = next_pts_; next_pts_ += frame_->nb_samples;
        if (avcodec_send_frame(context_, frame_) < 0) continue;
        AVPacket *packet = av_packet_alloc();
        while (packet && avcodec_receive_packet(context_, packet) == 0) {
          muxer_.audio(
            std::vector<std::uint8_t>(packet->data, packet->data + packet->size),
            static_cast<std::uint32_t>(packet->duration > 0 ? packet->duration : frame_->nb_samples));
          av_packet_unref(packet);
        }
        av_packet_free(&packet);
      }
    }

  private:
    fragment_muxer_t &muxer_;
    AVCodecContext *context_ {nullptr};
    AVFrame *frame_ {nullptr};
    std::vector<float> pending_;
    std::int64_t next_pts_ {0};
  };

  struct stream_context_t {
    std::string session_id;
    int fps;
    safe::mail_t mail {std::make_shared<safe::mail_raw_t>()};
    video::packet_queue_t video_packets;
    pipe_writer_t pipe;
    std::unique_ptr<fragment_muxer_t> muxer;
    std::unique_ptr<aac_encoder_t> aac;
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
      context->aac = std::make_unique<aac_encoder_t>(*context->muxer);
      auto raw = context.get();
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
      raw->audio_capture = std::thread([raw]() {
        audio::capture(raw->mail, audio_config(), raw, [raw](std::vector<float> &&samples) {
          raw->aac->push(std::move(samples));
        });
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

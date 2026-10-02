#include <cmath>
#include <cstdio>
#include <sstream>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <stdexcept>
#include <string>
#include <vector>
#include "zimt/zimtohrli.h"

namespace {
constexpr char revision[] = "f9e7364df2f6a41f761f513b7ea6be7e2d6f2ce3";
constexpr size_t rate = 48000;

std::vector<std::vector<float>> Read(const std::filesystem::path& path, size_t channels) {
  const auto bytes = std::filesystem::file_size(path);
  const auto stride = 4 * channels;
  if (bytes % stride != 0 || bytes < rate * stride || bytes > 30 * rate * stride)
    throw std::runtime_error("PCM must contain 1 to 30 seconds of complete frames.");
  const size_t frames = static_cast<size_t>(bytes / stride);
  std::ifstream input(path, std::ios::binary);
  if (!input) throw std::runtime_error("Cannot read PCM.");
  std::vector<std::vector<float>> result(channels, std::vector<float>(frames));
  for (size_t frame = 0; frame < frames; ++frame) {
    for (size_t channel = 0; channel < channels; ++channel) {
      unsigned char b[4];
      if (!input.read(reinterpret_cast<char*>(b), 4)) throw std::runtime_error("Truncated PCM.");
      const uint32_t bits = static_cast<uint32_t>(b[0]) | static_cast<uint32_t>(b[1]) << 8 |
          static_cast<uint32_t>(b[2]) << 16 | static_cast<uint32_t>(b[3]) << 24;
      float value;
      static_assert(sizeof(value) == sizeof(bits));
      std::memcpy(&value, &bits, sizeof(value));
      if (!std::isfinite(value) || std::abs(value) > 1)
        throw std::runtime_error("PCM has non-finite samples or samples outside [-1,1].");
      result[channel][frame] = value;
    }
  }
  if (input.peek() != std::char_traits<char>::eof()) throw std::runtime_error("PCM changed while reading.");
  return result;
}
}  // namespace

int Assess(const std::vector<std::filesystem::path>& args) {
  try {
    if (args.size() == 1 && args[0] == "--version") {
      std::cout << "{\"schema\":1,\"metric\":\"zimtohrli\",\"revision\":\"" << revision << "\"}\n";
      return 0;
    }
    if ((args.size() != 3 && args.size() != 5) || (args.size() == 5 && args[3] != "--report")
        || (args[2] != "1" && args[2] != "2"))
      throw std::runtime_error("Usage: optimisarr-audio-quality reference.f32le candidate.f32le channels(1|2) [--report new-file.json]");
    const size_t channels = args[2] == "1" ? 1 : 2;
    auto reference = Read(args[0], channels);
    auto candidate = Read(args[1], channels);
    if (reference[0].size() != candidate[0].size()) throw std::runtime_error("PCM frame counts differ.");
    const zimtohrli::Zimtohrli metric;
    std::vector<float> distances;
    for (size_t channel = 0; channel < channels; ++channel) {
      auto a = metric.Analyze(zimtohrli::Span<const float>(reference[channel]));
      auto b = metric.Analyze(zimtohrli::Span<const float>(candidate[channel]));
      const float distance = metric.Distance(a, b);
      if (!std::isfinite(distance) || distance < 0 || distance > 1)
        throw std::runtime_error("Metric returned an invalid distance.");
      distances.push_back(distance);
    }
    std::ostringstream json;
    json << std::setprecision(9) << "{\"schema\":1,\"metric\":\"zimtohrli\",\"revision\":\""
        << revision << "\",\"sampleRate\":48000,\"fullScaleSineDb\":" << metric.full_scale_sine_db
        << ",\"channels\":" << channels << ",\"frames\":" << reference[0].size() << ",\"distances\":[";
    for (size_t channel = 0; channel < channels; ++channel) {
      if (channel) json << ',';
      json << distances[channel];
    }
    json << "]}\n";
    const auto output = json.str();
    if (args.size() == 5) {
      FILE* report = nullptr;
#ifdef _WIN32
      _wfopen_s(&report, args[4].c_str(), L"wx");
#else
      report = std::fopen(args[4].c_str(), "wx");
#endif
      if (!report) throw std::runtime_error("Cannot create report; the destination must not exist.");
      const auto written = std::fwrite(output.data(), 1, output.size(), report);
      const auto closed = std::fclose(report);
      if (written != output.size() || closed != 0) throw std::runtime_error("Cannot complete report.");
    }
    std::cout << output;
    return 0;
  } catch (const std::exception& error) {
    std::cerr << error.what() << '\n';
    return 1;
  }
}

#ifdef _WIN32
int wmain(int argc, wchar_t** argv) {
#else
int main(int argc, char** argv) {
#endif
  std::vector<std::filesystem::path> args;
  for (int i = 1; i < argc; ++i) args.emplace_back(argv[i]);
  return Assess(args);
}

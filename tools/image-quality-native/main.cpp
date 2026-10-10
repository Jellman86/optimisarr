#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstdio>
#include <filesystem>
#include <fstream>
#include <memory>
#include <map>
#include <stdexcept>
#include <vector>

#include <jxl/cms.h>
#include "lib/extras/codec_in_out.h"
#include "lib/extras/dec/decode.h"
#include "lib/extras/packed_image_convert.h"
#include "lib/jxl/base/exif.h"
#include "lib/extras/size_constraints.h"
#include "tools/ssimulacra2.h"
#include "tools/no_memory_manager.h"
#include "webp/decode.h"
#include "webp/demux.h"
#include "zlib.h"

namespace {
constexpr uint64_t kMaximumPixels = 16000000;
constexpr uint64_t kMaximumFileBytes = 128 * 1024 * 1024;
constexpr size_t kMaximumMetadataBytes = 4 * 1024 * 1024;
void Require(bool condition, const char* reason) {
    if (!condition) throw std::runtime_error(reason);
}

std::vector<uint8_t> Read(const std::filesystem::path& path) {
    const auto length = std::filesystem::file_size(path);
    Require(length >= 12 && length <= kMaximumFileBytes, "Input must be between 12 bytes and 128 MiB.");
    std::vector<uint8_t> bytes(static_cast<size_t>(length));
    std::ifstream stream(path, std::ios::binary);
    Require(static_cast<bool>(stream.read(reinterpret_cast<char*>(bytes.data()), static_cast<std::streamsize>(length))), "Could not read the complete image.");
    return bytes;
}

void CheckExif(const std::vector<uint8_t>& exif) {
    if (exif.empty()) return;
    bool big;
    Require(jxl::IsExif(exif, &big), "Malformed EXIF cannot establish orientation.");
    auto u16 = [&](size_t i) { Require(i + 2 <= exif.size(), "Truncated EXIF."); return big ? LoadBE16(exif.data() + i) : LoadLE16(exif.data() + i); };
    auto u32 = [&](size_t i) { Require(i + 4 <= exif.size(), "Truncated EXIF."); return big ? LoadBE32(exif.data() + i) : LoadLE32(exif.data() + i); };
    const uint64_t offset = u32(4);
    Require(offset >= 8 && offset + 2 <= exif.size(), "Malformed EXIF directory.");
    const uint64_t count = u16(static_cast<size_t>(offset));
    Require(offset + 2 + count * 12 + 4 <= exif.size(), "Truncated EXIF directory.");
    bool found = false;
    for (uint64_t i = 0; i < count; ++i) {
        const size_t pos = static_cast<size_t>(offset + 2 + i * 12);
        if (u16(pos) != 274) continue;
        Require(!found && u16(pos + 2) == 3 && u32(pos + 4) == 1 && u16(pos + 8) == 1,
                "Only an unambiguous identity EXIF orientation is supported.");
        found = true;
    }
}

size_t CheckCompressedMetadata(const uint8_t* data, size_t size, size_t budget) {
    std::vector<uint8_t> output(budget + 1);
    uLongf length = static_cast<uLongf>(output.size());
    Require(uncompress(output.data(), &length, data, static_cast<uLong>(size)) == Z_OK && length <= budget,
            "Compressed PNG metadata is invalid or exceeds the combined 4 MiB budget.");
    return static_cast<size_t>(length);
}

// Decoders may ignore malformed or lower-priority colour metadata. A score must
// never certify pixels after silently discarding a declared profile or orientation.
void CheckContainerMetadata(const std::vector<uint8_t>& bytes, const jxl::extras::PackedPixelFile* ppf) {
    if (bytes[0] == 0x89 && bytes[1] == 'P') {
        bool icc = false, cicp = false, exif = false, srgb = false;
        bool ended = false;
        size_t metadata_bytes = 0;
        auto account_metadata = [&](size_t size) {
            Require(size <= kMaximumMetadataBytes - metadata_bytes, "PNG metadata exceeds the combined 4 MiB budget.");
            metadata_bytes += size;
        };
        for (size_t pos = 8; pos < bytes.size();) {
            Require(bytes.size() - pos >= 12, "Truncated PNG chunk.");
            const size_t size = LoadBE32(bytes.data() + pos);
            Require(size <= bytes.size() - pos - 12, "Truncated PNG payload.");
            const auto* kind = bytes.data() + pos + 4;
            const auto* data = kind + 4;
            auto is = [&](const char* name) { return std::equal(kind, kind + 4, name); };
            if (is("iCCP") || is("zTXt") || is("iTXt") || is("tEXt")) {
                account_metadata(size + 12);
                const auto* end = std::find(data, data + size, 0);
                const size_t keyword = static_cast<size_t>(end - data);
                Require(keyword > 0 && keyword <= 79 && keyword < size, "Malformed PNG metadata keyword.");
                constexpr char legacy[] = "Raw profile type ";
                Require(keyword < sizeof(legacy) - 1 || !std::equal(data, data + sizeof(legacy) - 1, legacy), "Legacy PNG raw profiles are unsupported.");
                if (is("iCCP") || is("zTXt")) {
                    Require(keyword + 2 < size && data[keyword + 1] == 0, "Malformed PNG metadata compression.");
                    account_metadata(CheckCompressedMetadata(data + keyword + 2, size - keyword - 2, kMaximumMetadataBytes - metadata_bytes));
                } else if (is("iTXt")) {
                    Require(keyword + 3 < size && data[keyword + 1] <= 1 && data[keyword + 2] == 0, "Malformed PNG international text.");
                    const auto* language = std::find(data + keyword + 3, data + size, 0);
                    Require(language < data + size, "Truncated PNG language tag.");
                    const auto* translated = std::find(language + 1, data + size, 0);
                    Require(translated < data + size, "Truncated PNG translated keyword.");
                    if (data[keyword + 1]) account_metadata(CheckCompressedMetadata(translated + 1, static_cast<size_t>(data + size - translated - 1), kMaximumMetadataBytes - metadata_bytes));
                    else Require(size <= kMaximumMetadataBytes, "PNG metadata exceeds 4 MiB.");
                } else Require(size <= kMaximumMetadataBytes, "PNG metadata exceeds 4 MiB.");
            }
            if (is("iCCP")) { Require(!icc && !cicp && !srgb && (!ppf || !ppf->icc.empty()), "Ambiguous or discarded PNG ICC profile."); icc = true; }
            if (is("cICP")) {
                Require(!cicp && !icc && !srgb && size == 4 && data[0] == 1 && data[1] == 13 && data[2] == 0 && data[3] == 1,
                        "Only unambiguous sRGB PNG cICP is qualified."); cicp = true;
            }
            if (is("sRGB")) { Require(!srgb && !icc && !cicp && size == 1 && data[0] <= 3, "Ambiguous PNG sRGB declaration."); srgb = true; }
            if (is("gAMA")) Require(size == 4 && LoadBE32(data) == 45455, "Non-sRGB PNG gamma is unsupported.");
            if (is("cHRM")) {
                constexpr uint32_t expected[] = {31270, 32900, 64000, 33000, 30000, 60000, 15000, 6000};
                Require(size == 32, "Malformed PNG chromaticities.");
                for (size_t i = 0; i < 8; ++i) Require(LoadBE32(data + 4 * i) == expected[i], "Non-sRGB PNG chromaticities are unsupported.");
            }
            if (is("eXIf")) {
                account_metadata(size + 12);
                Require(size > 0 && size <= kMaximumMetadataBytes && !exif && (!ppf || std::vector<uint8_t>(data, data + size) == ppf->metadata.exif), "Ambiguous, oversized or discarded PNG EXIF."); exif = true;
            }
            if (is("acTL")) Require(false, "Animated PNG is unsupported.");
            pos += size + 12;
            if (is("IEND")) { Require(size == 0 && pos == bytes.size(), "Malformed end or trailing PNG data."); ended = true; break; }
        }
        Require(ended, "Missing PNG end marker.");
    } else if (bytes[0] == 0xff && bytes[1] == 0xd8) {
        std::map<unsigned, std::vector<uint8_t>> fragments;
        unsigned count = 0;
        bool exif = false;
        bool ended = false;
        size_t pos = 2;
        while (pos < bytes.size()) {
            // Skip entropy-coded bytes, including escaped FF and restart markers.
            if (bytes[pos++] != 0xff) continue;
            while (pos < bytes.size() && bytes[pos] == 0xff) ++pos;
            Require(pos < bytes.size(), "Truncated JPEG marker.");
            const auto marker = bytes[pos++];
            if (marker == 0 || (marker >= 0xd0 && marker <= 0xd7) || marker == 1) continue;
            if (marker == 0xd9) { Require(pos == bytes.size(), "Trailing JPEG data is unsupported."); ended = true; break; }
            Require(bytes.size() - pos >= 2, "Truncated JPEG segment.");
            const size_t size = LoadBE16(bytes.data() + pos);
            Require(size >= 2 && size <= bytes.size() - pos, "Malformed JPEG segment.");
            const auto* data = bytes.data() + pos + 2;
            const size_t length = size - 2;
            if (marker == 0xe2 && length >= 12 && std::equal(data, data + 12, "ICC_PROFILE\0")) {
                Require(length > 14 && data[12] > 0 && data[13] > 0 && data[12] <= data[13], "Malformed JPEG ICC fragment.");
                Require((count == 0 || count == data[13]) && fragments.count(data[12]) == 0, "Ambiguous JPEG ICC fragments.");
                count = data[13]; fragments.emplace(data[12], std::vector<uint8_t>(data + 14, data + length));
            }
            if (marker == 0xe1 && length >= 6 && std::equal(data, data + 6, "Exif\0\0")) {
                Require(length > 6 && !exif && (!ppf || std::vector<uint8_t>(data + 6, data + length) == ppf->metadata.exif), "Ambiguous or discarded JPEG EXIF."); exif = true;
            }
            pos += size;
        }
        Require(ended, "Missing JPEG end marker.");
        if (count) {
            Require(fragments.size() == count, "Incomplete JPEG ICC profile.");
            std::vector<uint8_t> profile;
            for (unsigned i = 1; i <= count; ++i) {
                Require(profile.size() + fragments.at(i).size() <= kMaximumMetadataBytes, "JPEG ICC exceeds 4 MiB.");
                profile.insert(profile.end(), fragments.at(i).begin(), fragments.at(i).end());
            }
            Require(!profile.empty() && (!ppf || profile == ppf->icc), "Discarded JPEG ICC profile.");
        }
    }
}

jxl::extras::PackedPixelFile Decode(const std::vector<uint8_t>& bytes) {
    CheckContainerMetadata(bytes, nullptr);
    jxl::extras::PackedPixelFile ppf;
    const jxl::SizeConstraints constraints = {16384, 16384, kMaximumPixels};
    if (bytes.size() >= 12 && std::equal(bytes.begin(), bytes.begin() + 4, "RIFF")
        && std::equal(bytes.begin() + 8, bytes.begin() + 12, "WEBP")) {
        WebPBitstreamFeatures features;
        Require(WebPGetFeatures(bytes.data(), bytes.size(), &features) == VP8_STATUS_OK, "Malformed WebP.");
        Require(!features.has_animation, "Animated WebP is unsupported.");
        Require(features.width >= 8 && features.height >= 8 && static_cast<uint64_t>(features.width) * features.height <= kMaximumPixels,
                "WebP exceeds the supported dimensions or 16 megapixel limit.");
        WebPData data = {bytes.data(), bytes.size()};
        std::unique_ptr<WebPDemuxer, decltype(&WebPDemuxDelete)> demux(WebPDemux(&data), WebPDemuxDelete);
        Require(demux != nullptr, "Incomplete WebP container.");
        Require(static_cast<uint64_t>(LoadLE32(bytes.data() + 4)) + 8 == bytes.size(), "Incomplete or trailing WebP data.");
        Require((WebPDemuxGetI(demux.get(), WEBP_FF_FORMAT_FLAGS) & ANIMATION_FLAG) == 0, "Animated WebP is unsupported.");
        ppf.info.xsize = static_cast<uint32_t>(features.width);
        ppf.info.ysize = static_cast<uint32_t>(features.height);
        ppf.info.bits_per_sample = 8;
        ppf.info.num_color_channels = 3;
        ppf.info.alpha_bits = features.has_alpha ? 8 : 0;
        ppf.info.orientation = JXL_ORIENT_IDENTITY;
        ppf.info.uses_original_profile = JXL_TRUE;
        JxlColorEncodingSetToSRGB(&ppf.color_encoding, JXL_FALSE);
        JxlPixelFormat format = {4, JXL_TYPE_UINT8, JXL_NATIVE_ENDIAN, 0};
        auto frame = jxl::extras::PackedFrame::Create(ppf.info.xsize, ppf.info.ysize, format);
        Require(frame.ok(), "Could not allocate WebP pixels.");
        auto decoded = std::move(frame).value_();
        Require(WebPDecodeRGBAInto(bytes.data(), bytes.size(), static_cast<uint8_t*>(decoded.color.pixels()),
                    decoded.color.pixels_size, static_cast<int>(decoded.color.stride)) != nullptr, "WebP decode failed.");
        ppf.frames.push_back(std::move(decoded));
        for (const char* name : {"ICCP", "EXIF"}) {
            WebPChunkIterator chunk;
            if (WebPDemuxGetChunk(demux.get(), name, 1, &chunk)) {
                const bool valid = chunk.num_chunks == 1 && chunk.chunk.size > 0 && chunk.chunk.size <= kMaximumMetadataBytes;
                if (!valid) { WebPDemuxReleaseChunkIterator(&chunk); Require(false, "Ambiguous, oversized or empty WebP metadata."); }
                auto& destination = name[0] == 'I' ? ppf.icc : ppf.metadata.exif;
                destination.assign(chunk.chunk.bytes, chunk.chunk.bytes + chunk.chunk.size);
                WebPDemuxReleaseChunkIterator(&chunk);
            }
        }
        const auto flags = WebPDemuxGetI(demux.get(), WEBP_FF_FORMAT_FLAGS);
        Require(((flags & ICCP_FLAG) != 0) == !ppf.icc.empty() && ((flags & EXIF_FLAG) != 0) == !ppf.metadata.exif.empty(), "Missing or undeclared WebP metadata.");
        if (!ppf.icc.empty()) ppf.primary_color_representation = jxl::extras::PackedPixelFile::kIccIsPrimary;
    } else {
        const auto codec = jxl::extras::DetectCodec(jxl::Bytes(bytes));
        Require(codec == jxl::extras::Codec::kPNG || codec == jxl::extras::Codec::kJPG,
                "Only PNG, JPEG and WebP SDR still images are qualified.");
        Require(static_cast<bool>(jxl::extras::DecodeBytes(jxl::Bytes(bytes), jxl::extras::ColorHints(), &ppf, &constraints)), "Image decode failed.");
    }
    CheckContainerMetadata(bytes, &ppf);
    Require(ppf.frames.size() == 1 && !ppf.info.have_animation, "Animation is unsupported.");
    Require(ppf.info.xsize >= 8 && ppf.info.ysize >= 8 && static_cast<uint64_t>(ppf.info.xsize) * ppf.info.ysize <= kMaximumPixels,
            "Image must be at least 8x8 and at most 16 megapixels.");
    Require(ppf.info.bits_per_sample == 8 && ppf.info.exponent_bits_per_sample == 0
            && !ppf.info.alpha_premultiplied, "Only straight-alpha 8-bit images are qualified.");
    Require(ppf.info.orientation == JXL_ORIENT_IDENTITY, "Non-identity orientation is unsupported.");
    CheckExif(ppf.metadata.exif);
    // The upstream converter may replace an invalid ICC with sRGB. Refuse it before that fallback.
    jxl::ColorEncoding color;
    if (!ppf.icc.empty()) {
        Require(static_cast<bool>(color.SetICC(jxl::IccBytes(ppf.icc), JxlGetDefaultCms())), "Invalid ICC profile.");
    } else {
        Require(static_cast<bool>(color.FromExternal(ppf.color_encoding)), "Invalid colour encoding.");
    }
    Require(color.IsSRGB(), "Only recognised SDR sRGB colour profiles are qualified.");
    return ppf;
}

float Alpha(const jxl::ImageBundle& image, size_t x, size_t y) {
    return image.HasAlpha() ? image.alpha()->Row(y)[x] : 1.0f;
}

int Measure(const std::filesystem::path& reference, const std::filesystem::path& candidate) {
    auto first = Decode(Read(reference));
    auto second = Decode(Read(candidate));
    Require(first.info.xsize == second.info.xsize && first.info.ysize == second.info.ysize,
            "Dimensions differ; downscaled comparisons are not qualified.");
    auto* memory = jpegxl::tools::NoMemoryManager();
    jxl::CodecInOut original(memory), distorted(memory);
    Require(static_cast<bool>(jxl::extras::ConvertPackedPixelFileToCodecInOut(first, nullptr, &original))
            && static_cast<bool>(jxl::extras::ConvertPackedPixelFileToCodecInOut(second, nullptr, &distorted)), "Pixel conversion failed.");
    float alpha_error = 0;
    for (size_t y = 0; y < original.ysize(); ++y)
        for (size_t x = 0; x < original.xsize(); ++x)
            alpha_error = std::max(alpha_error, std::abs(Alpha(original.Main(), x, y) - Alpha(distorted.Main(), x, y)));
    const bool alpha = original.Main().HasAlpha() || distorted.Main().HasAlpha();
    std::vector<double> scores;
    for (float background : alpha ? std::vector<float>{0.1f, 0.9f} : std::vector<float>{0.5f}) {
        auto metric = ComputeSSIMULACRA2(original.Main(), distorted.Main(), background);
        Require(metric.ok(), "Metric calculation failed.");
        const double score = std::move(metric).value_().Score();
        Require(std::isfinite(score) && score <= 100.000001, "Metric returned an invalid score.");
        scores.push_back(std::min(score, 100.0));
    }
    std::printf("{\"schema\":1,\"metric\":\"ssimulacra2\",\"revision\":\"a7a9c787341cf703dede03c2009fa460cae5e5df\","
                "\"preparation\":\"sdr-srgb-native-still-v1\",\"width\":%u,\"height\":%u,\"alpha\":%s,\"maximumAlphaError\":%.9g,"
                "\"score\":%.12g,\"backgroundScores\":[", first.info.xsize, first.info.ysize, alpha ? "true" : "false",
                static_cast<double>(alpha_error), *std::min_element(scores.begin(), scores.end()));
    for (size_t i = 0; i < scores.size(); ++i) std::printf("%s%.12g", i ? "," : "", scores[i]);
    std::puts("]}");
    return 0;
}
}  // namespace

#ifdef _WIN32
int wmain(int argc, wchar_t** argv) {
#else
int main(int argc, char** argv) {
#endif
    try {
        Require(argc == 3, "Usage: optimisarr-image-quality original candidate");
        return Measure(std::filesystem::path(argv[1]), std::filesystem::path(argv[2]));
    } catch (const std::exception& error) {
        std::fprintf(stderr, "%s\n", error.what());
        return 1;
    }
}

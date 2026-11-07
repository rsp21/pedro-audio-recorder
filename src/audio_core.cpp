#include "audio_core.hpp"

#include <algorithm>
#include <cmath>
#include <limits>

namespace pedro_audio {

namespace {
float sample_as_mono(std::span<const float> input, std::size_t channels, std::size_t frame) {
    if (channels == 0 || frame * channels >= input.size()) return 0.0f;
    float sum = 0.0f;
    const auto base = frame * channels;
    for (std::size_t c = 0; c < channels; ++c) sum += input[base + c];
    return sum / static_cast<float>(channels);
}
}

void mix_to_stereo(std::span<const float> render, std::size_t render_channels,
                   std::span<const float> microphone, std::size_t microphone_channels,
                   std::span<float> destination, MixConfig config) {
    const std::size_t frames = destination.size() / 2;
    for (std::size_t frame = 0; frame < frames; ++frame) {
        const float r = sample_as_mono(render, render_channels, frame) * config.render_gain;
        const float m = sample_as_mono(microphone, microphone_channels, frame) * config.microphone_gain;
        const float mixed = std::clamp(r + m, -1.0f, 1.0f);
        destination[frame * 2] = mixed;
        destination[frame * 2 + 1] = mixed;
    }
}

std::vector<int16_t> float_to_pcm16(std::span<const float> samples) {
    std::vector<int16_t> result;
    result.reserve(samples.size());
    for (const float sample : samples) {
        const float bounded = std::clamp(sample, -1.0f, 1.0f);
        const float scaled = bounded < 0.0f ? bounded * 32768.0f : bounded * 32767.0f;
        result.push_back(static_cast<int16_t>(std::lrint(scaled)));
    }
    return result;
}

} // namespace pedro_audio

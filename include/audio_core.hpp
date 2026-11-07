#pragma once

#include <cstddef>
#include <cstdint>
#include <span>
#include <vector>

namespace pedro_audio {

struct MixConfig {
    float render_gain = 1.0f;
    float microphone_gain = 1.0f;
};

// Mixes mono or multichannel float PCM into a stereo interleaved destination.
// Input spans contain interleaved samples. Missing input is treated as silence.
// Output is hard-limited to [-1, 1] to prevent integer conversion clipping.
void mix_to_stereo(std::span<const float> render,
                   std::size_t render_channels,
                   std::span<const float> microphone,
                   std::size_t microphone_channels,
                   std::span<float> destination,
                   MixConfig config = {});

std::vector<int16_t> float_to_pcm16(std::span<const float> samples);

} // namespace pedro_audio

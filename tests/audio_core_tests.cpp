#include "audio_core.hpp"

#include <cassert>
#include <cmath>
#include <iostream>
#include <span>

using pedro_audio::MixConfig;

void test_mixes_render_and_microphone_without_clipping() {
    const float render[] = {0.4f, 0.4f};
    const float microphone[] = {0.3f, 0.3f};
    float output[4]{};
    pedro_audio::mix_to_stereo(render, 1, microphone, 1, output, {});
    for (float value : output) assert(std::fabs(value - 0.7f) < 0.0001f);
}

void test_mixer_hard_limits_sum() {
    const float render[] = {0.9f};
    const float microphone[] = {0.8f};
    float output[2]{};
    pedro_audio::mix_to_stereo(render, 1, microphone, 1, output, {});
    assert(output[0] == 1.0f && output[1] == 1.0f);
}

void test_pcm16_conversion_is_bounded() {
    const float input[] = {-1.2f, -1.0f, 0.0f, 1.0f, 1.2f};
    const auto output = pedro_audio::float_to_pcm16(input);
    assert(output.size() == 5);
    assert(output[0] == -32768);
    assert(output[1] == -32768);
    assert(output[2] == 0);
    assert(output[3] == 32767);
    assert(output[4] == 32767);
}

int main() {
    test_mixes_render_and_microphone_without_clipping();
    test_mixer_hard_limits_sum();
    test_pcm16_conversion_is_bounded();
    std::cout << "audio_core_tests: PASS\n";
}

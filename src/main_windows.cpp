#define NOMINMAX
#include <windows.h>
#include <mmdeviceapi.h>
#include <audioclient.h>
#include <avrt.h>
#include <functiondiscoverykeys_devpkey.h>
#include <ksmedia.h>
#include <wrl/client.h>

#include <atomic>
#include <chrono>
#include <condition_variable>
#include <cstdint>
#include <deque>
#include <fstream>
#include <iostream>
#include <mutex>
#include <string>
#include <thread>
#include <vector>
#include <algorithm>
#include <cmath>
#include <limits>


#include "audio_core.hpp"

using Microsoft::WRL::ComPtr;
using namespace std::chrono_literals;

namespace {

void log_hr(const char* stage, HRESULT hr) {
    std::cerr << stage << " hr=0x" << std::hex
              << static_cast<unsigned long>(hr) << std::dec << "\n";
}


class SampleQueue {
    std::mutex mutex_;
    std::condition_variable cv_;
    std::deque<float> samples_;
public:
    void push(const std::vector<float>& samples) {
        if (samples.empty()) return;
        {
            std::lock_guard<std::mutex> lock(mutex_);
            samples_.insert(samples_.end(), samples.begin(), samples.end());
        }
        cv_.notify_all();
    }

    bool pop(float& value, const std::atomic<bool>& running) {
        std::unique_lock<std::mutex> lock(mutex_);
        if (!cv_.wait_for(lock, 100ms, [&] { return !samples_.empty() || !running.load(); })) return false;
        if (samples_.empty()) return false;
        value = samples_.front();
        samples_.pop_front();
        return true;
    }

    void wake() { cv_.notify_all(); }
};

class WavWriter {
    std::ofstream file_;
    uint32_t data_bytes_ = 0;
    uint32_t sample_rate_ = 48000;

    void write_header() {
        const uint32_t riff_size = 36u + data_bytes_;
        const uint32_t fmt_size = 16;
        const uint16_t pcm = 1;
        const uint16_t channels = 2;
        const uint16_t bits = 16;
        const uint16_t block_align = channels * bits / 8;
        const uint32_t byte_rate = sample_rate_ * block_align;
        file_.seekp(0);
        file_.write("RIFF", 4);
        file_.write(reinterpret_cast<const char*>(&riff_size), 4);
        file_.write("WAVEfmt ", 8);
        file_.write(reinterpret_cast<const char*>(&fmt_size), 4);
        file_.write(reinterpret_cast<const char*>(&pcm), 2);
        file_.write(reinterpret_cast<const char*>(&channels), 2);
        file_.write(reinterpret_cast<const char*>(&sample_rate_), 4);
        file_.write(reinterpret_cast<const char*>(&byte_rate), 4);
        file_.write(reinterpret_cast<const char*>(&block_align), 2);
        file_.write(reinterpret_cast<const char*>(&bits), 2);
        file_.write("data", 4);
        file_.write(reinterpret_cast<const char*>(&data_bytes_), 4);
        file_.seekp(0, std::ios::end);
    }
public:
    bool open(const std::string& requested_path, uint32_t sample_rate) {
        char absolute[MAX_PATH]{};
        const DWORD n = GetFullPathNameA(requested_path.c_str(), MAX_PATH, absolute, nullptr);
        const std::string path = n ? std::string(absolute, n) : requested_path;
        std::cerr << "wav_open path=" << path << "\n";
        sample_rate_ = sample_rate;
        file_.open(path, std::ios::binary | std::ios::trunc);
        if (!file_) return false;
        char blank[44]{};
        file_.write(blank, sizeof(blank));
        write_header();
        return static_cast<bool>(file_);
    }

    bool write_pcm16(const std::vector<int16_t>& pcm) {
        if (!file_) return false;
        const auto bytes = static_cast<uint32_t>(pcm.size() * sizeof(int16_t));
        file_.write(reinterpret_cast<const char*>(pcm.data()), bytes);
        data_bytes_ += bytes;
        return static_cast<bool>(file_);
    }

    bool close() {
        if (!file_) return false;
        write_header();
        file_.close();
        std::cerr << "wav_close data_bytes=" << data_bytes_ << "\n";
        return true;
    }
};

std::wstring endpoint_name(IMMDevice* device) {
    ComPtr<IPropertyStore> store;
    PROPVARIANT value;
    PropVariantInit(&value);
    if (FAILED(device->OpenPropertyStore(STGM_READ, &store)) ||
        FAILED(store->GetValue(PKEY_Device_FriendlyName, &value))) {
        PropVariantClear(&value);
        return L"";
    }
    std::wstring result = value.vt == VT_LPWSTR && value.pwszVal ? value.pwszVal : L"";
    PropVariantClear(&value);
    return result;
}

ComPtr<IMMDevice> find_endpoint(IMMDeviceEnumerator* enumerator, EDataFlow flow, const wchar_t* preferred) {
    ComPtr<IMMDeviceCollection> collection;
    HRESULT hr = enumerator->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, &collection);
    if (FAILED(hr)) { log_hr("EnumAudioEndpoints", hr); return {}; }
    UINT count = 0;
    hr = collection->GetCount(&count);
    if (FAILED(hr)) { log_hr("GetCount", hr); return {}; }
    for (UINT i = 0; i < count; ++i) {
        ComPtr<IMMDevice> device;
        if (FAILED(collection->Item(i, &device))) continue;
        const std::wstring name = endpoint_name(device.Get());
        if (name.find(preferred) != std::wstring::npos &&
            (flow == eRender || name.find(L"Microphone") != std::wstring::npos)) return device;
    }
    ComPtr<IMMDevice> fallback;
    hr = enumerator->GetDefaultAudioEndpoint(flow, eConsole, &fallback);
    if (FAILED(hr)) log_hr("GetDefaultAudioEndpoint", hr);
    return fallback;
}

struct CaptureArgs {
    ComPtr<IAudioClient> client;
    WAVEFORMATEX* format = nullptr;
    SampleQueue* queue = nullptr;
    std::atomic<bool>* running = nullptr;
    bool loopback = false;
    const char* label = "capture";
    float* level = nullptr;
};

float read_sample(const BYTE* address, WORD bits, bool is_float) {
    if (is_float && bits == 32) return *reinterpret_cast<const float*>(address);
    if (!is_float && bits == 16) return static_cast<float>(*reinterpret_cast<const int16_t*>(address)) / 32768.0f;
    if (!is_float && bits == 32) return static_cast<float>(*reinterpret_cast<const int32_t*>(address)) / 2147483648.0f;
    return 0.0f;
}

bool format_is_float(const WAVEFORMATEX* format) {
    if (format->wFormatTag == WAVE_FORMAT_IEEE_FLOAT) return true;
    if (format->wFormatTag == WAVE_FORMAT_EXTENSIBLE && format->cbSize >= 22) {
        const auto* ext = reinterpret_cast<const WAVEFORMATEXTENSIBLE*>(format);
        return IsEqualGUID(ext->SubFormat, KSDATAFORMAT_SUBTYPE_IEEE_FLOAT) != FALSE;
    }
    return false;
}

void capture_thread(CaptureArgs args) {
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    HANDLE avrt = nullptr;
    auto cleanup = [&] {
        if (avrt) AvRevertMmThreadCharacteristics(avrt);
        if (args.format) CoTaskMemFree(args.format);
        args.queue->wake();
        CoUninitialize();
    };

    const DWORD flags = args.loopback ? AUDCLNT_STREAMFLAGS_LOOPBACK : 0;
    hr = args.client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, 10000000, 0, args.format, nullptr);

    if (FAILED(hr)) { log_hr(args.loopback ? "loopback Initialize" : "microphone Initialize", hr); cleanup(); return; }

    ComPtr<IAudioCaptureClient> capture;
    hr = args.client->GetService(__uuidof(IAudioCaptureClient), reinterpret_cast<void**>(capture.GetAddressOf()));

    if (FAILED(hr) || !capture) { log_hr(args.loopback ? "loopback GetService" : "microphone GetService", hr); cleanup(); return; }

    hr = args.client->Start();

    if (FAILED(hr)) { log_hr(args.loopback ? "loopback Start" : "microphone Start", hr); cleanup(); return; }
    std::cerr << args.label << " started rate=" << args.format->nSamplesPerSec
              << " channels=" << args.format->nChannels
              << " bits=" << args.format->wBitsPerSample << "\n";

    const bool is_float = format_is_float(args.format);
    const size_t bytes_per_sample = args.format->nBlockAlign / args.format->nChannels;
    uint64_t frames_seen = 0;
    while (args.running->load()) {
        UINT32 packet_frames = 0;
        hr = capture->GetNextPacketSize(&packet_frames);
        if (FAILED(hr)) { log_hr(args.loopback ? "loopback GetNextPacketSize" : "microphone GetNextPacketSize", hr); break; }
        if (packet_frames == 0) { std::this_thread::sleep_for(2ms); continue; }

        while (packet_frames > 0 && args.running->load()) {
            BYTE* data = nullptr;
            DWORD buffer_flags = 0;
            UINT64 device_position = 0;
            UINT64 qpc_position = 0;
            const UINT32 requested = packet_frames;
            hr = capture->GetBuffer(&data, &packet_frames, &buffer_flags, &device_position, &qpc_position);
            if (FAILED(hr)) { log_hr(args.loopback ? "loopback GetBuffer" : "microphone GetBuffer", hr); break; }

            std::vector<float> mono(packet_frames, 0.0f);
            if ((buffer_flags & AUDCLNT_BUFFERFLAGS_SILENT) == 0 && data != nullptr) {
                for (UINT32 frame = 0; frame < packet_frames; ++frame) {
                    float sum = 0.0f;
                    for (WORD channel = 0; channel < args.format->nChannels; ++channel) {
                        const BYTE* sample = data + frame * args.format->nBlockAlign + channel * bytes_per_sample;
                        sum += read_sample(sample, args.format->wBitsPerSample, is_float);
                    }
                    mono[frame] = std::clamp(sum / static_cast<float>(args.format->nChannels), -1.0f, 1.0f);
                }
            }
            args.queue->push(mono);
            if (args.level) {
                float peak = 0.0f;
                for (float sample : mono) peak = std::max(peak, std::abs(sample));
                *args.level = std::max(*args.level * 0.75f, peak);
            }
            frames_seen += packet_frames;
            hr = capture->ReleaseBuffer(packet_frames);
            if (FAILED(hr)) { log_hr(args.loopback ? "loopback ReleaseBuffer" : "microphone ReleaseBuffer", hr); break; }
            packet_frames = 0;
            hr = capture->GetNextPacketSize(&packet_frames);
            if (FAILED(hr)) { log_hr(args.loopback ? "loopback GetNextPacketSize" : "microphone GetNextPacketSize", hr); break; }
            (void)requested;
        }
    }
    std::cerr << args.label << " frames=" << frames_seen << "\n";
    hr = args.client->Stop();
    if (FAILED(hr) && hr != AUDCLNT_E_NOT_INITIALIZED) log_hr(args.loopback ? "loopback Stop" : "microphone Stop", hr);

    cleanup();
}

bool inspect_format(const char* label, WAVEFORMATEX* format) {
    if (!format || format->nChannels == 0 || format->nBlockAlign == 0 || format->nSamplesPerSec == 0) return false;
    std::cerr << label << " format tag=" << format->wFormatTag
              << " rate=" << format->nSamplesPerSec
              << " channels=" << format->nChannels
              << " bits=" << format->wBitsPerSample
              << " block=" << format->nBlockAlign << "\n";
    return true;
}

} // namespace

int main(int argc, char** argv) {

    std::cerr << "boot\n";
    HRESULT hr = CoInitializeEx(nullptr, COINIT_MULTITHREADED);
    if (FAILED(hr) && hr != RPC_E_CHANGED_MODE) { log_hr("main CoInitializeEx", hr); return 3; }

    ComPtr<IMMDeviceEnumerator> enumerator;
    hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL, IID_PPV_ARGS(&enumerator));
    if (FAILED(hr)) { log_hr("CoCreateInstance MMDeviceEnumerator", hr); return 3; }

    if (argc > 1 && std::string(argv[1]) == "--devices") {
        for (const auto flow : {eRender, eCapture}) {
            ComPtr<IMMDeviceCollection> collection;
            if (FAILED(enumerator->EnumAudioEndpoints(flow, DEVICE_STATE_ACTIVE, &collection))) continue;
            UINT count = 0; collection->GetCount(&count);
            for (UINT i = 0; i < count; ++i) {
                ComPtr<IMMDevice> device; collection->Item(i, &device);
                std::wcout << (flow == eRender ? L"Render: " : L"Capture: ") << endpoint_name(device.Get()) << L"\n";
            }
        }
        if (SUCCEEDED(hr)) CoUninitialize();
        return 0;
    }

    const std::string output = argc > 1 ? argv[1] : "pedro-audio.wav";
    int seconds = 0;
    std::string stop_event_name;
    std::string pause_event_name;
    std::string control_id;
    for (int i = 1; i + 1 < argc; ++i) {
        if (std::string(argv[i]) == "--seconds") seconds = std::max(1, std::stoi(argv[i + 1]));
        if (std::string(argv[i]) == "--stop-event") stop_event_name = argv[i + 1];
        if (std::string(argv[i]) == "--pause-event") pause_event_name = argv[i + 1];
        if (std::string(argv[i]) == "--control-id") control_id = argv[i + 1];
    }
    HANDLE stop_event = stop_event_name.empty() ? nullptr : OpenEventA(SYNCHRONIZE, FALSE, stop_event_name.c_str());
    HANDLE pause_event = pause_event_name.empty() ? nullptr : OpenEventA(SYNCHRONIZE, FALSE, pause_event_name.c_str());
    HANDLE control_mapping = control_id.empty() ? nullptr : OpenFileMappingA(FILE_MAP_ALL_ACCESS, FALSE, ("Local\\RSPAudioLevels_" + control_id).c_str());
    float* levels = control_mapping ? static_cast<float*>(MapViewOfFile(control_mapping, FILE_MAP_ALL_ACCESS, 0, 0, sizeof(float) * 2)) : nullptr;

    ComPtr<IMMDevice> render = find_endpoint(enumerator.Get(), eRender, L"Poly BT700");
    if (!render) { std::cerr << "render endpoint selection failed\n"; return 4; }
    std::wcout << L"Render: " << endpoint_name(render.Get()) << L"\n";

    ComPtr<IAudioClient> render_client;
    hr = render->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, reinterpret_cast<void**>(render_client.GetAddressOf()));
    if (FAILED(hr)) { log_hr("render Activate", hr); return 5; }

    WAVEFORMATEX* render_format = nullptr;
    hr = render_client->GetMixFormat(&render_format);
    if (FAILED(hr)) { log_hr("render GetMixFormat", hr); return 6; }
    if (!inspect_format("render", render_format)) return 6;

    WavWriter wav;
    if (!wav.open(output, 48000)) { std::cerr << "Cannot open output\n"; CoTaskMemFree(render_format); return 2; }

    SampleQueue render_queue;
    std::atomic<bool> running{true};
    CaptureArgs render_args{render_client, render_format, &render_queue, &running, true, "loopback", levels ? &levels[0] : nullptr};
    const uint32_t render_rate = render_format->nSamplesPerSec;
    std::cerr << "starting loopback thread\n";
    std::thread render_thread(capture_thread, render_args);
    std::cerr << "capture thread launched\n";

    std::atomic<bool> stop_input{false};
    std::thread input_thread;
    if (seconds == 0 && !stop_event) input_thread = std::thread([&] { std::string ignored; std::getline(std::cin, ignored); stop_input = true; });
    const auto deadline = seconds > 0 ? std::chrono::steady_clock::now() + std::chrono::seconds(seconds) : std::chrono::steady_clock::time_point::max();

    double render_accumulator = 0.0;
    float last_render = 0.0f;
    uint64_t output_frames = 0;
    while (std::chrono::steady_clock::now() < deadline && !stop_input.load()) {
        if (stop_event && WaitForSingleObject(stop_event, 0) == WAIT_OBJECT_0) break;
        if (pause_event && WaitForSingleObject(pause_event, 0) == WAIT_OBJECT_0) {
            std::this_thread::sleep_for(50ms);
            continue;
        }
        render_accumulator += static_cast<double>(render_rate);
        while (render_accumulator >= 48000.0) {
            float sample = 0.0f;
            if (render_queue.pop(sample, running)) last_render = sample;
            render_accumulator -= 48000.0;
        }
        std::vector<float> mixed{last_render, last_render};
        if (!wav.write_pcm16(pedro_audio::float_to_pcm16(mixed))) { std::cerr << "WAV write failed\n"; break; }
        ++output_frames;
    }

    running = false;
    render_queue.wake();
    render_thread.join();
    if (input_thread.joinable()) { stop_input = true; input_thread.join(); }
    wav.close();
    if (stop_event) CloseHandle(stop_event);
    if (pause_event) CloseHandle(pause_event);
    if (levels) UnmapViewOfFile(levels);
    if (control_mapping) CloseHandle(control_mapping);
    std::cerr << "output_frames=" << output_frames << "\n";
    if (SUCCEEDED(hr)) CoUninitialize();
    return output_frames > 0 ? 0 : 7;
}

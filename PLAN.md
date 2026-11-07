# Pedro Audio Recorder — implementation plan

## Scope

Build a small x64 Windows executable that records the active Windows playback stream and the Poly BT700 microphone simultaneously, without changing Windows defaults, installing a virtual audio driver, or depending on Snipping Tool internals.

## Phases

1. Portable mixer core — complete
   - Timestamp-independent initial mixing contract.
   - Saturating float-to-PCM16 conversion.
   - Sandbox test pass.
2. Windows device layer
   - Enumerate render/capture endpoints with MMDevice API.
   - Select active render endpoint for WASAPI loopback.
   - Select Poly BT700 microphone by endpoint name, with explicit device override.
3. Capture engines
   - `IAudioClient` loopback render capture.
   - `IAudioClient` microphone capture.
   - Event-driven buffers; no default-device changes.
4. Synchronization/mixing
   - QPC timestamps.
   - Resampling/channel normalization.
   - Bounded gain and clipping protection.
5. File output/UI
   - WAV first for deterministic verification.
   - Minimal console/tray control; no screen capture.
6. Windows acceptance test
   - Browser tone + spoken phrase.
   - Confirm both signals in the resulting file.
   - Confirm endpoint defaults before/after are unchanged.

## Build constraint

The bot-it Linux host lacks a Windows C++ toolchain and CMake. Pedro’s PC has .NET SDK 9.0.301 but no `cl.exe`/CMake. The portable core was compiled and passed in the Proxmox sandbox with `g++`; the Windows WASAPI layer still requires a Windows-capable build path. No code or binaries have been installed on Pedro’s PC.

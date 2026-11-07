# Pedro Audio Recorder

Small Windows 11 audio-only recorder for combining:

- WASAPI loopback from the active Windows render endpoint (the Poly BT700 playback path).
- WASAPI capture from the selected microphone endpoint (initially Poly BT700).
- Timestamp-aware software mixing into one WAV file.

This project does not use Stereo Mix, `Listen to this device`, OBS, a virtual audio driver, or Snipping Tool internals. It uses public Windows WASAPI/Media Foundation-compatible APIs and leaves Windows audio defaults unchanged.

## Current status

Initial implementation slice: portable mixer contract and test-first scaffolding. Windows endpoint enumeration/capture and WAV finalization are next.

## Intended acceptance

1. Start/stop without changing the default render or capture device.
2. Capture system playback through WASAPI loopback.
3. Capture Poly BT700 microphone independently.
4. Mix both sources with bounded gain and no clipping.
5. Produce a playable audio-only file.
6. Continue normal PC use during recording.

## Build target

The final executable targets Windows 11 x64 and will be built with the Visual Studio C++ toolchain or Windows SDK. The current Linux host does not have a Windows C++ toolchain installed, so native Windows compilation must be performed in a Windows build environment before endpoint deployment.

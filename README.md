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

## Live transcript (proof of concept)

Each recording mixes the system audio (WASAPI loopback) with your microphone (the Poly BT700 headset mic when it is connected, otherwise the default recording device). The output is timed by the clock, so silence is kept at its real length. Turn the microphone off under **Settings → Record my microphone**. On the command line, the core takes `--no-mic`.

While a recording runs, the recorder starts `live_transcriber.py`. It follows the WAV as it is written and transcribes it with [faster-whisper](https://github.com/SYSTRAN/faster-whisper): large-v3 on the GPU, or small on the CPU when there is no GPU. Text appears about 1–2 seconds behind the audio. The **RSP Transcripts** window (`build\viewer\RSPTranscripts.exe`) opens next to the recorder and follows the live recording. It also lists every past transcript by day, with search, copy, export to `.txt`, play and show-in-folder.

- **Python:** uses `%USERPROFILE%\RSP-Transcript\.venv` (faster-whisper with CUDA, created by that project's `setup.ps1`). To use a different interpreter, set the `RSP_PYTHON` environment variable or the `PythonPath` value under `HKCU\Software\RSP\AudioRecorder`.
- **Files:** each recording gets `<name>.transcript.jsonl` (the saved transcript) next to its `.wav` in `Music\Audio Recorder`. While it is recording there is also a temporary `<name>.live.json`. The file format is described at the top of `transcriber/live_transcriber.py`.
- **Controls:** the page icon on the left of the recorder opens the transcript window. **Settings → Live transcript** turns transcription on or off.
- **Options:** `RSP_WHISPER_MODEL` (for example `small`) and `RSP_WHISPER_LANGUAGE` (for example `en` or `es`). Without a language setting, the language is detected once there are about 4 seconds of actual speech, and then kept. Until then, words show only as the gray live line and nothing is saved.
- **Logs:** `%LOCALAPPDATA%\RSP-Audio-Recorder\transcriber.log`. Set `RSP_TRANSCRIBER_DEBUG=1` to log each transcription pass.

The streaming works like this: about once a second, the audio after the last committed word is transcribed again. A word is committed once two passes in a row agree on it. Words that are not committed yet are shown as the gray live line.

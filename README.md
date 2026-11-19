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

## The app

`build\pedro-audio-recorder.exe` is one window: the recorder controls on top, and the live transcript with every past recording below. It runs in the background like the built-in Windows tools:

- **Ctrl+Shift+Space** shows and hides it (you can change the shortcut in **Settings**).
- It never appears in the taskbar, Alt+Tab or Task View. Closing or minimising only hides it.
- It starts with Windows, hidden (**Settings → Start with Windows**). **Settings → Quit** closes it completely.
- The page icon on the left collapses it to the compact recorder card and expands it again.
- Keys while it is open: **Space** records, pauses and resumes. **S** stops and saves. **Esc** hides the window.
- Command line: `--background` starts it hidden, and `--quit` closes the running copy (`build_gui.bat` uses this before rebuilding).

Build everything with `build_gui.bat`. It builds the C++ capture core, copies the transcription worker, and builds the WPF app (`app/`, .NET 9) into `build\`.

## Recording and live transcript

Each recording mixes the system audio (WASAPI loopback) with your microphone (the Poly BT700 headset mic when it is connected, otherwise the default recording device) into `<name>.wav`. The output is timed by the clock, so silence is kept at its real length. Turn the microphone off under **Settings → Record my microphone**. On the command line, the core takes `--no-mic`.

The core also writes `<name>.sources.wav` while recording: 16 kHz, with PC audio on the left channel and the microphone on the right. `live_transcriber.py` transcribes each channel separately with [faster-whisper](https://github.com/SYSTRAN/faster-whisper) (large-v3 on the GPU, small on the CPU). Every line in the transcript is labelled **Microphone** or **PC audio**. Text appears about 1–2 seconds behind the audio. The sources file is deleted once the transcript is finished.

- **Python:** uses `%USERPROFILE%\RSP-Transcript\.venv` (faster-whisper with CUDA, created by that project's `setup.ps1`). To use a different interpreter, set the `RSP_PYTHON` environment variable or the `PythonPath` value under `HKCU\Software\RSP\AudioRecorder`.
- **Files:** each recording gets `<name>.transcript.jsonl` (the saved transcript, one line per segment, with its source) next to its `.wav` in `Music\Audio Recorder`. While it is recording there is also a temporary `<name>.live.json`. The format is described at the top of `transcriber/live_transcriber.py`.
- **Transcript view:** recordings are grouped by day, with search across all of them. **Copy** and **Export** produce text like `[00:04] Microphone: …`. There are also **Play** and **Show in folder**.
- **Options:** `RSP_WHISPER_MODEL` (for example `small`) and `RSP_WHISPER_LANGUAGE` (for example `en` or `es`). Without a language setting, the language is detected once there are about 4 seconds of actual speech, and then kept. Until then, words show only as the gray live line and nothing is saved.
- **Logs:** `%LOCALAPPDATA%\RSP-Audio-Recorder\transcriber.log` and `app.log`. Set `RSP_TRANSCRIBER_DEBUG=1` to log each transcription pass.

The streaming works like this, for each source: about once a second, the audio after the last committed word is transcribed again. A word is committed once two passes in a row agree on it. Words that are not committed yet are shown as the gray live line.

If you use speakers instead of a headset, the microphone also picks up the PC audio. Those lines then appear twice, once labelled Microphone and once labelled PC audio.

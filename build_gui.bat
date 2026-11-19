@echo off
call "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
cd /d "C:\Users\Pedro Sanhueza\pedro-audio-recorder"
if not exist build mkdir build

rem The recorder runs in the background; close it so its files can be replaced.
if exist build\pedro-audio-recorder.exe start "" /wait build\pedro-audio-recorder.exe --quit
taskkill /im RSPTranscripts.exe >nul 2>&1

rem Capture core (C++): system audio + microphone -> WAV, plus the per-source file for the transcript.
cl /nologo /std:c++20 /EHsc /O2 /Iinclude /c src\audio_core.cpp /Fo:build\audio_core.obj
cl /nologo /std:c++20 /EHsc /O2 /Iinclude /c src\main_windows.cpp /Fo:build\main_windows.obj
link /nologo /SUBSYSTEM:CONSOLE build\main_windows.obj build\audio_core.obj ole32.lib avrt.lib mmdevapi.lib /OUT:build\pedro-audio-recorder-core.exe

rem Live transcription worker (Python, faster-whisper) next to the exe.
copy /y transcriber\live_transcriber.py build\live_transcriber.py >nul

rem The app (WPF): recorder + live transcript + history in one window.
dotnet build app\RSPAudioRecorder.csproj -c Release -o build -nologo -v q

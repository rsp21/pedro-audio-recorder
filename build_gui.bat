@echo off
call "C:\Program Files (x86)\Microsoft Visual Studio\2019\BuildTools\VC\Auxiliary\Build\vcvars64.bat"
cd /d "C:\Users\Pedro Sanhueza\pedro-audio-recorder"
if not exist build mkdir build
cl /nologo /std:c++20 /EHsc /O2 /Iinclude /c src\audio_core.cpp /Fo:build\audio_core.obj
cl /nologo /std:c++20 /EHsc /O2 /Iinclude /c src\main_windows.cpp /Fo:build\main_windows.obj
link /nologo /SUBSYSTEM:CONSOLE build\main_windows.obj build\audio_core.obj ole32.lib avrt.lib mmdevapi.lib /OUT:build\pedro-audio-recorder-core.exe
cl /nologo /std:c++20 /EHsc /O2 /c src\recorder_gui.cpp /Fobuild\recorder_gui.obj
rc /nologo /fo build\recorder_gui.res src\recorder_gui.rc
rem The GUI runs in the background; close it so the exe can be replaced.
if exist build\pedro-audio-recorder.exe start "" /wait build\pedro-audio-recorder.exe --quit
link /nologo /SUBSYSTEM:WINDOWS build\recorder_gui.obj build\recorder_gui.res ole32.lib user32.lib gdi32.lib shell32.lib gdiplus.lib dwmapi.lib comctl32.lib advapi32.lib /OUT:build\pedro-audio-recorder.exe

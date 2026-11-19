cmake_minimum_required(VERSION 3.20)
project(pedro_audio_recorder LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

option(PEDRO_AUDIO_BUILD_TESTS "Build portable mixer tests" ON)

add_library(audio_core STATIC
    src/audio_core.cpp
)
target_include_directories(audio_core PUBLIC include)

if(PEDRO_AUDIO_BUILD_TESTS)
    enable_testing()
    add_executable(audio_core_tests tests/audio_core_tests.cpp)
    target_link_libraries(audio_core_tests PRIVATE audio_core)
    add_test(NAME audio_core_tests COMMAND audio_core_tests)
endif()

if(WIN32)
    add_executable(pedro-audio-recorder-core
        src/main_windows.cpp
    )
    target_link_libraries(pedro-audio-recorder-core PRIVATE audio_core ole32 avrt mmdevapi)
    target_compile_definitions(pedro-audio-recorder-core PRIVATE UNICODE _UNICODE NOMINMAX)
endif()

# The app itself (recorder + live transcript) is the WPF project in app/;
# build it with: dotnet build app/RSPAudioRecorder.csproj -c Release -o build

cmake_minimum_required(VERSION 3.16)
project(TqkLibraryStreamRelayDemuxFFmpegNative LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_VISIBILITY_PRESET hidden)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

set(TARGET_NAME "TqkLibrary.StreamRelay.Demux.FFmpeg.Native")
set(WORKER_NAME "TqkLibrary.StreamRelay.DemuxWorker")

# ---------------------------------------------------------------------------
# FFmpeg inputs (from TqkLibrary.FFmpeg.Native.<RID> NuGet packages), supplied
# by Build.ps1 / CI:
#   FFMPEG_INCLUDE_DIR  -> <pkg>/build/native/include            (all platforms)
#   FFMPEG_LIB_DIR      -> <pkg>/build/native/win/<arch>/lib     (Windows import libs)
#   FFMPEG_RUNTIME_DIR  -> <pkg>/runtimes/<rid>/native           (Linux .so to link)
# ---------------------------------------------------------------------------
if(NOT DEFINED FFMPEG_INCLUDE_DIR)
    message(FATAL_ERROR "FFMPEG_INCLUDE_DIR is not set (path to <pkg>/build/native/include)")
endif()
if(NOT EXISTS "${FFMPEG_INCLUDE_DIR}/libavformat/avformat.h")
    message(FATAL_ERROR "avformat.h not found under FFMPEG_INCLUDE_DIR=${FFMPEG_INCLUDE_DIR}")
endif()

# Version for the Windows version resource (defaults keep local builds working).
if(NOT DEFINED VER_FILE_MAJOR)
    set(VER_FILE_MAJOR 0)
endif()
if(NOT DEFINED VER_FILE_MINOR)
    set(VER_FILE_MINOR 0)
endif()
if(NOT DEFINED VER_FILE_BUILD)
    set(VER_FILE_BUILD 0)
endif()
configure_file(
    "${CMAKE_CURRENT_SOURCE_DIR}/version.generated.h.in"
    "${CMAKE_CURRENT_BINARY_DIR}/version.generated.h"
    @ONLY)

set(SHARED_SOURCES
    Exports.cpp
    Demuxer.cpp
    Muxer.cpp)
if(WIN32)
    list(APPEND SHARED_SOURCES dllmain.cpp version.rc)
endif()

add_library(${TARGET_NAME} SHARED ${SHARED_SOURCES})
set_target_properties(${TARGET_NAME} PROPERTIES OUTPUT_NAME "${TARGET_NAME}" PREFIX "")

# Out-of-process worker exe (M5). Links the same demux core directly.
add_executable(${WORKER_NAME} Worker.cpp Demuxer.cpp)
set_target_properties(${WORKER_NAME} PROPERTIES OUTPUT_NAME "${WORKER_NAME}")

function(configure_ffmpeg tgt)
    target_include_directories(${tgt} PRIVATE
        "${CMAKE_CURRENT_SOURCE_DIR}"
        "${CMAKE_CURRENT_BINARY_DIR}"   # version.generated.h
        "${FFMPEG_INCLUDE_DIR}")

    if(WIN32)
        if(NOT DEFINED FFMPEG_LIB_DIR)
            message(FATAL_ERROR "FFMPEG_LIB_DIR is not set (path to <pkg>/build/native/win/<arch>/lib)")
        endif()
        foreach(lib avformat avcodec avutil)
            find_library(FFLIB_${lib} NAMES ${lib} PATHS "${FFMPEG_LIB_DIR}" NO_DEFAULT_PATH)
            if(NOT FFLIB_${lib})
                message(FATAL_ERROR "Import lib ${lib}.lib not found in FFMPEG_LIB_DIR=${FFMPEG_LIB_DIR}")
            endif()
            target_link_libraries(${tgt} PRIVATE "${FFLIB_${lib}}")
        endforeach()
        if(MSVC)
            target_compile_options(${tgt} PRIVATE /utf-8 /W3)
        endif()
    else()
        if(NOT DEFINED FFMPEG_RUNTIME_DIR)
            message(FATAL_ERROR "FFMPEG_RUNTIME_DIR is not set (path to <pkg>/runtimes/<rid>/native)")
        endif()
        set_target_properties(${tgt} PROPERTIES BUILD_RPATH "$ORIGIN" INSTALL_RPATH "$ORIGIN")
        foreach(lib avformat avcodec avutil)
            file(GLOB FFLIB_${lib} "${FFMPEG_RUNTIME_DIR}/lib${lib}.so*")
            if(NOT FFLIB_${lib})
                message(FATAL_ERROR "lib${lib}.so* not found in FFMPEG_RUNTIME_DIR=${FFMPEG_RUNTIME_DIR}")
            endif()
            list(GET FFLIB_${lib} 0 FFLIB_${lib}_FIRST)
            target_link_libraries(${tgt} PRIVATE "${FFLIB_${lib}_FIRST}")
        endforeach()
    endif()
endfunction()

configure_ffmpeg(${TARGET_NAME})
configure_ffmpeg(${WORKER_NAME})

if(NOT WIN32)
    set_target_properties(${TARGET_NAME} PROPERTIES PREFIX "lib")
endif()

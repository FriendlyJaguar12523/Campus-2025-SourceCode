cmake_minimum_required(VERSION 3.16)

project(tachyon LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# LiteNetLibPP (the LiteNetLib transport) is a pinned git submodule under vendor/. It carries no
# packet-layer hook for the RecRoom 0x11 marker, so we apply patches/0001-tachyon-packet-layer-hook
# to its working tree here, idempotently, before building it. See patches/README.md.
set(LNLPP_DIR ${CMAKE_CURRENT_SOURCE_DIR}/vendor/LiteNetLibPP)
set(LNLPP_PATCH ${CMAKE_CURRENT_SOURCE_DIR}/patches/0001-tachyon-packet-layer-hook.patch)

if (NOT EXISTS ${LNLPP_DIR}/CMakeLists.txt)
    message(FATAL_ERROR "vendor/LiteNetLibPP is empty. Run: git submodule update --init --recursive")
endif ()

find_package(Git QUIET REQUIRED)
# If the patch reverse-applies cleanly it is already present; otherwise apply it forward.
execute_process(
    COMMAND ${GIT_EXECUTABLE} -C ${LNLPP_DIR} apply --reverse --check ${LNLPP_PATCH}
    RESULT_VARIABLE LNLPP_ALREADY_PATCHED OUTPUT_QUIET ERROR_QUIET)
if (NOT LNLPP_ALREADY_PATCHED EQUAL 0)
    execute_process(
        COMMAND ${GIT_EXECUTABLE} -C ${LNLPP_DIR} apply ${LNLPP_PATCH}
        RESULT_VARIABLE LNLPP_PATCH_RESULT)
    if (NOT LNLPP_PATCH_RESULT EQUAL 0)
        message(FATAL_ERROR "Failed to apply ${LNLPP_PATCH} to LiteNetLibPP")
    endif ()
    message(STATUS "Applied Tachyon packet-layer hook to LiteNetLibPP")
else ()
    message(STATUS "LiteNetLibPP packet-layer hook already applied")
endif ()

# Turn off LiteNetLibPP's tests (they fetch a googletest incompatible with modern CMake) and examples.
set(BUILD_EXAMPLE OFF CACHE BOOL "" FORCE)
set(BUILD_AND_RUN_TESTS OFF CACHE BOOL "" FORCE)
add_subdirectory(vendor/LiteNetLibPP)

# libcurl: the room lookup (AccountId -> roomInstanceId) calls recflare over HTTP(S).
find_package(CURL REQUIRED)
# OpenSSL: decrypt the client's RSA-encrypted wire blobs with the configured private key.
find_package(OpenSSL REQUIRED)

add_executable(tachyon
    src/main.cpp
    src/tachyon_server.cpp
    src/dissonance.cpp
    src/hashing.cpp
    src/room_lookup.cpp
    src/config.cpp
    src/crypto.cpp)

target_compile_features(tachyon PRIVATE cxx_std_23)
target_include_directories(tachyon PRIVATE
    ${CMAKE_CURRENT_SOURCE_DIR}/src
    ${CMAKE_CURRENT_SOURCE_DIR}/third_party)
target_link_libraries(tachyon PRIVATE lnl CURL::libcurl OpenSSL::Crypto)

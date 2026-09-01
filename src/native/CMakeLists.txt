cmake_minimum_required(VERSION 3.16)

project(SherlockProfiler CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

option(SHERLOCK_ASAN "Build with Address/UndefinedBehavior sanitizers (dev/test)" OFF)
option(SHERLOCK_BUILD_TESTS "Build native unit tests" OFF)
option(SHERLOCK_NATIVE_ARCH "Tune for the build machine's CPU (-march=native); unsafe for shipped builds" OFF)

find_program(CCACHE_PROGRAM ccache)
if(CCACHE_PROGRAM)
    set(CMAKE_CXX_COMPILER_LAUNCHER "${CCACHE_PROGRAM}" CACHE STRING "Compiler launcher" FORCE)
endif()

include_directories(include)
include_directories(SYSTEM common common/sdk)

if(NOT WIN32)
    add_compile_options(
        -Wno-invalid-noreturn
        -Wno-pragma-pack
        -Wno-int-to-pointer-cast
        -fPIC
        -fms-extensions
        -DBIT64
        -DPAL_STDCPP_COMPAT
        -DPLATFORM_UNIX
        -DHOST_64BIT)
    add_compile_options(-Wall -Wextra)
    add_compile_options(-fstack-protector-strong)
    add_compile_options($<$<NOT:$<CONFIG:Debug>>:-D_FORTIFY_SOURCE=2>)
    add_link_options(-pthread)
    include_directories(SYSTEM common/unix)

    add_compile_options($<$<NOT:$<CONFIG:Debug>>:-O3>)
    add_compile_options($<$<NOT:$<CONFIG:Debug>>:-funroll-loops>)
    if(SHERLOCK_NATIVE_ARCH)
        add_compile_options($<$<NOT:$<CONFIG:Debug>>:-march=native>)
    elseif(CMAKE_SYSTEM_PROCESSOR MATCHES "^(x86_64|amd64|AMD64)$")
        add_compile_options($<$<NOT:$<CONFIG:Debug>>:-march=x86-64-v2>)
    endif()

    add_compile_options(-fvisibility=hidden -fvisibility-inlines-hidden -ftls-model=initial-exec)
    add_compile_options("-ffile-prefix-map=${CMAKE_SOURCE_DIR}=.")
endif()

include(CheckIPOSupported)
check_ipo_supported(RESULT SHERLOCK_IPO_OK OUTPUT SHERLOCK_IPO_MSG)
if(SHERLOCK_IPO_OK)
    set(CMAKE_INTERPROCEDURAL_OPTIMIZATION_RELEASE ON)
    set(CMAKE_INTERPROCEDURAL_OPTIMIZATION_RELWITHDEBINFO ON)
endif()

if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    add_link_options(-Wl,-z,relro,-z,now)
    add_link_options(-Wl,--build-id)
    if(NOT SHERLOCK_ASAN)
        add_link_options(-static-libstdc++ -static-libgcc)
    endif()
endif()

if(SHERLOCK_ASAN AND NOT WIN32)
    add_compile_options(-fsanitize=address,undefined -fno-omit-frame-pointer)
    add_link_options(-fsanitize=address,undefined)
endif()

set(SOURCES
    common/guids.cpp
    src/dllmain.cpp
    src/common/logger.cpp
    src/profiler/profiler.cpp
    src/profiler/aggregator.cpp
    src/profiler/entrypoint.cpp
    src/profiler/shadowstack.cpp
    src/profiler/il_writer.cpp
    src/profiler/probe.cpp
    src/profiler/triggers.cpp
    src/control/channel.cpp)

if(WIN32)
    list(APPEND SOURCES src/profiler.def)
    add_compile_definitions(_CRT_SECURE_NO_WARNINGS)
endif(WIN32)

add_library(SherlockProfiler SHARED ${SOURCES})

if(WIN32)
    target_link_libraries(SherlockProfiler PRIVATE ws2_32)
endif()

if(SHERLOCK_BUILD_TESTS)
    enable_testing()
    add_subdirectory(tests)
endif()

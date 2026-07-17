cmake_minimum_required(VERSION 3.14)

project(SherlockProfiler CXX)

set(CMAKE_CXX_STANDARD 23)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_CXX_EXTENSIONS OFF)

# The allocation callback (ObjectAllocated -> Aggregator::record) runs on every sampled allocation in
# the target, so this library is genuinely hot — default to a fully optimized build; Debug is opt-in.
if(NOT CMAKE_BUILD_TYPE AND NOT CMAKE_CONFIGURATION_TYPES)
    set(CMAKE_BUILD_TYPE Release CACHE STRING "Build type" FORCE)
endif()

option(SHERLOCK_ASAN "Build with Address/UndefinedBehavior sanitizers (dev/test)" OFF)
option(SHERLOCK_NATIVE_ARCH "Tune for the build machine's CPU (-march=native); turn OFF for portable/packaged builds" ON)

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

    # Optimized (non-Debug) builds: aggressive optimization for the allocation hot path.
    add_compile_options($<$<NOT:$<CONFIG:Debug>>:-O3>)
    add_compile_options($<$<NOT:$<CONFIG:Debug>>:-funroll-loops>)
    if(SHERLOCK_NATIVE_ARCH)
        add_compile_options($<$<NOT:$<CONFIG:Debug>>:-march=native>)
    endif()
endif()

# Link-time optimization for optimized builds — lets the hot path (Aggregator::record, hashFrames)
# inline across the profiler/aggregator translation units.
include(CheckIPOSupported)
check_ipo_supported(RESULT SHERLOCK_IPO_OK OUTPUT SHERLOCK_IPO_MSG)
if(SHERLOCK_IPO_OK)
    set(CMAKE_INTERPROCEDURAL_OPTIMIZATION_RELEASE ON)
    set(CMAKE_INTERPROCEDURAL_OPTIMIZATION_RELWITHDEBINFO ON)
endif()

if(CMAKE_SYSTEM_NAME STREQUAL "Linux")
    add_link_options(-Wl,-z,relro,-z,now)
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
    src/profiler/trace.cpp
    src/profiler/probe.cpp
    src/profiler/triggers.cpp
    src/control/channel.cpp)

if(WIN32)
    list(APPEND SOURCES src/profiler.def)
    add_compile_definitions(_CRT_SECURE_NO_WARNINGS) # std::getenv
endif(WIN32)

add_library(SherlockProfiler SHARED ${SOURCES})

enable_testing()
add_subdirectory(tests)

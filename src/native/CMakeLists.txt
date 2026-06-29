cmake_minimum_required(VERSION 3.14)

project(SherlockProfiler CXX)

set(CMAKE_CXX_STANDARD 17)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

include_directories(common)
include_directories(common/sdk)
include_directories(include)

if(NOT WIN32)
    add_compile_options(-Wno-invalid-noreturn -Wno-pragma-pack -Wno-int-to-pointer-cast -fPIC -fms-extensions -DBIT64 -DPAL_STDCPP_COMPAT -DPLATFORM_UNIX -DHOST_64BIT)
    add_link_options(-pthread)
    include_directories(common/unix)
endif(NOT WIN32)

set(SOURCES
    common/guids.cpp
    src/dllmain.cpp
    src/common/logger.cpp
    src/profiler/profiler.cpp
    src/profiler/aggregator.cpp
    src/profiler/trace.cpp)

if(WIN32)
    list(APPEND SOURCES src/profiler.def)
    add_compile_definitions(_CRT_SECURE_NO_WARNINGS) # std::getenv
endif(WIN32)

add_library(SherlockProfiler SHARED ${SOURCES})

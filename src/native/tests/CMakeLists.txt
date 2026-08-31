find_package(GTest CONFIG QUIET)
if(NOT GTest_FOUND)
    include(FetchContent)
    set(BUILD_GMOCK OFF CACHE BOOL "" FORCE)
    set(INSTALL_GTEST OFF CACHE BOOL "" FORCE)
    set(gtest_force_shared_crt ON CACHE BOOL "" FORCE)
    FetchContent_Declare(
        googletest
        GIT_REPOSITORY https://github.com/google/googletest.git
        GIT_TAG 52eb8108c5bdec04579160ae17225d66034bd723)
    FetchContent_MakeAvailable(googletest)
endif()

add_executable(SherlockTests
    aggregator.cpp
    coherent_capture.cpp
    container.cpp
    control_channel.cpp
    intervals.cpp
    probes.cpp
    profile.cpp
    symbols.cpp
    ${CMAKE_SOURCE_DIR}/src/common/logger.cpp
    ${CMAKE_SOURCE_DIR}/src/control/channel.cpp
    ${CMAKE_SOURCE_DIR}/src/profiler/aggregator.cpp
    ${CMAKE_SOURCE_DIR}/src/profiler/probe.cpp)
target_link_libraries(SherlockTests PRIVATE GTest::gtest GTest::gtest_main)
if(WIN32)
    target_link_libraries(SherlockTests PRIVATE ws2_32)
endif()
add_test(NAME SherlockTests COMMAND SherlockTests)

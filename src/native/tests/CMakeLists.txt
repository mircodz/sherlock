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
    container.cpp
    intervals.cpp
    profile.cpp
    symbols.cpp
    ${CMAKE_SOURCE_DIR}/src/common/logger.cpp
    ${CMAKE_SOURCE_DIR}/src/profiler/aggregator.cpp)
target_link_libraries(SherlockTests PRIVATE GTest::gtest GTest::gtest_main)
add_test(NAME SherlockTests COMMAND SherlockTests)
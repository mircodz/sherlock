find_package(GTest QUIET)
if(GTest_FOUND)
    add_executable(SherlockTests
        intervals.cpp container.cpp symbols.cpp profile.cpp aggregator.cpp
        ${CMAKE_SOURCE_DIR}/src/profiler/aggregator.cpp)
    target_link_libraries(SherlockTests PRIVATE GTest::gtest GTest::gtest_main)
    add_test(NAME SherlockTests COMMAND SherlockTests)
endif()
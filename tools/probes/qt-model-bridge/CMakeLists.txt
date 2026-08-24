cmake_minimum_required(VERSION 3.21)

project(NsightSolidProbe LANGUAGES CXX)

set(CMAKE_AUTOMOC ON)
set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

find_package(Qt6 6.8.1 EXACT REQUIRED COMPONENTS Core Gui Widgets)

add_library(solidprobe MODULE
    solid_probe_plugin.cpp
    solidprobe.json
)
target_link_libraries(solidprobe PRIVATE Qt6::Core Qt6::Gui Qt6::Widgets)
set_target_properties(solidprobe PROPERTIES
    PREFIX ""
    LIBRARY_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out/generic"
    LIBRARY_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/out/generic"
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out/generic"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/out/generic"
)

add_executable(solidprobe_host host_main.cpp)
target_link_libraries(solidprobe_host PRIVATE Qt6::Core Qt6::Gui Qt6::Widgets)
set_target_properties(solidprobe_host PROPERTIES
    RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/out"
    RUNTIME_OUTPUT_DIRECTORY_RELEASE "${CMAKE_BINARY_DIR}/out"
)

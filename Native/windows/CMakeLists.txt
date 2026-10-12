cmake_minimum_required(VERSION 3.20)
project(Joycon2Ble LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
# Static CRT: the DLL must load in the Editor and in player builds without a VC++ redistributable.
set(CMAKE_MSVC_RUNTIME_LIBRARY "MultiThreaded$<$<CONFIG:Debug>:Debug>")

add_library(Joycon2Ble SHARED src/joycon2ble.cpp include/joycon2ble.h)
target_include_directories(Joycon2Ble PRIVATE include)
target_compile_definitions(Joycon2Ble PRIVATE JC2_EXPORTS WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(Joycon2Ble PRIVATE /W4 /permissive- /utf-8 /EHsc /bigobj)
target_link_libraries(Joycon2Ble PRIVATE windowsapp)

# Companion tool: boosts the connection interval of Joy-Con 2 connected by a browser (Web Bluetooth). Not part of Unity.
add_executable(Joycon2Boost tools/joycon2boost.cpp)
target_compile_definitions(Joycon2Boost PRIVATE WIN32_LEAN_AND_MEAN NOMINMAX)
target_compile_options(Joycon2Boost PRIVATE /W4 /permissive- /utf-8 /EHsc)
target_link_libraries(Joycon2Boost PRIVATE windowsapp)

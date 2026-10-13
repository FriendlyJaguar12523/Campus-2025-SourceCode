cmake_minimum_required(VERSION 3.4.1)
project(libcampus)

add_library(libcampus SHARED main.cpp)

target_compile_definitions(libcampus PRIVATE PLATFORM_QUEST=1)
target_link_libraries(libcampus log dl)
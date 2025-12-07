cmake_minimum_required(VERSION 3.15)

project(DamaServer LANGUAGES CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)

add_executable(dama_server
    src/main.cpp
    src/protocol.cpp
    src/handlers.cpp
)

target_include_directories(dama_server PRIVATE ${CMAKE_CURRENT_SOURCE_DIR}/src)
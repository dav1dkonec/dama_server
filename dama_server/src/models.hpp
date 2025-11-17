#pragma once

#include <string>
#include <vector>
#include <cstddef>
#include <netinet/in.h>

// Maximální počet hráčů v místnosti
constexpr std::size_t ROOM_CAPACITY = 2;

// Hráč
struct Player {
    int id = 0;
    std::string nick;
    sockaddr_in addr{};
};

// Stav místnosti
enum class RoomStatus {
    WAITING,
    IN_GAME,
    FINISHED
};

// Kdo je na tahu
enum class Turn {
    NONE,
    PLAYER1,
    PLAYER2
};

// Herní místnost
struct Room {
    int id = 0;
    std::string name;
    RoomStatus status = RoomStatus::WAITING;
    std::vector<std::string> playerKeys; // identifikace hráčů podle clientKey ("ip:port")
    Turn turn = Turn::NONE;
};
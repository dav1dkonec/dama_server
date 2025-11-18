#pragma once

#include <string>
#include <vector>
#include <cstddef>
#include <netinet/in.h>

// Herní globální config
constexpr std::size_t ROOM_CAPACITY = 2;
constexpr int BOARD_SIZE = 8;

// Hráč
struct Player {
    int id = 0;
    std::string nick;
    sockaddr_in addr{}; // adresa hráče, kam se posílají Message
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
    std:string board; // hrací deska (8x8)
};


// === Pomocné funkce pro práci s deskou ===

// Vytvoří počáteční rozložení kamenů pro dámu
inline std::string createInitialBoard() {
    std::string b;
    b.resize(BOARD_SIZE * BOARD_SIZE, '.');

    for (int r = 0; r < BOARD_SIZE; ++r) {
        for (int c = 0; c < BOARD_SIZE; ++c) {
            bool dark = ((r + c) % 2 == 1);
            int idx = r * BOARD_SIZE + c;

            if (!dark) continue;

            if (r < 3) {
                b[idx] = 'b';
            } else if (r > 4) {
                b[idx] = 'w';
            }
        }
    }

    return b;
}

// Vrátí figurku na pozici (row, col)
inline char getPiece(const Room& room, int row, int col) {
    int idx = row * BOARD_SIZE + col;
    if (idx < 0 || idx >= static_cast<int>(room.board.size())) {
        return '.'; // pojistka
    }
    return room.board[idx];
}

// Nastaví figurku na pozici (row, col)
inline void setPiece(Room& room, int row, int col, char piece) {
    int idx = row * BOARD_SIZE + col;
    if (idx < 0 || idx >= static_cast<int>(room.board.size())) {
        return; // pojistka
    }
    room.board[idx] = piece;
}
#include "handlers.hpp"

#include <iostream>
#include <sstream>
#include <algorithm>
#include <cmath>
#include <arpa/inet.h> // inet_ntop, sendto, ...

// Lokální pomocné funkce jen pro tento soubor
namespace {

std::string turnToString(Turn t) {
    switch (t) {
        case Turn::PLAYER1: return "PLAYER1";
        case Turn::PLAYER2: return "PLAYER2";
        default:            return "NONE";
    }
}

// Pošle všem hráčům v místnosti zprávu GAME_STATE
// Odpověď: ID;GAME_STATE;room=<roomId>;turn=<PLAYER1|PLAYER2|NONE>;board=<64 znaků>
void broadcastGameState(
    int msgId,
    const Room& room,
    const PlayersMap& players,
    int sockfd
) {
    for (const auto& pKey : room.playerKeys) {
        auto pit = players.find(pKey);
        if (pit == players.end()) continue;

        const Player& p = pit->second;
        sockaddr_in pAddr = p.addr;
        socklen_t pLen = sizeof(pAddr);

        std::string resp = std::to_string(msgId) +
                           ";GAME_STATE;room=" + std::to_string(room.id) +
                           ";turn=" + turnToString(room.turn) +
                           ";board=" + room.board + "\n";

        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&pAddr), pLen);
    }
}

} // namespace

// LOGIN
// Klient → server:  ID;LOGIN;<nick>
// Server → klient:  ID;LOGIN_OK;player=<playerId>
// nebo:             ID;ERROR;INVALID_FORMAT;Missing nick
void handleLogin(
    const Message& msg,
    const std::string& clientKey,
    PlayersMap& players,
    int& nextPlayerId,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
) {
    if (msg.rawParams.size() < 1) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Missing nick\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    std::string nick = msg.rawParams[0];

    Player p;
    p.id   = nextPlayerId++;
    p.nick = nick;
    p.addr = clientAddr;

    players[clientKey] = p;

    std::cout << "New player: id=" << p.id
              << " nick=" << p.nick
              << " from " << clientKey << std::endl;

    std::string resp = std::to_string(msg.id) +
                       ";LOGIN_OK;player=" + std::to_string(p.id) + "\n";

    sendto(sockfd, resp.c_str(), resp.size(), 0,
           reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
}

// PING
// Klient → server:  ID;PING
// Server → klient:  ID;PONG
void handlePing(
    const Message& msg,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
) {
    std::string resp = std::to_string(msg.id) + ";PONG\n";
    sendto(sockfd, resp.c_str(), resp.size(), 0,
           reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
}

// LIST_ROOMS
// Klient → server:  ID;LIST_ROOMS
// Server → klient:  ID;ROOMS_EMPTY
//    nebo pro každou room: ID;ROOM;id=<id>;name=<name>;players=<count>;status=<WAITING|IN_GAME|FINISHED>
void handleListRooms(
    const Message& msg,
    const RoomsMap& rooms,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
) {
    if (rooms.empty()) {
        std::string resp = std::to_string(msg.id) +
                           ";ROOMS_EMPTY\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    for (const auto& [roomId, room] : rooms) {
        std::stringstream ss;
        ss << msg.id << ";ROOM;"
           << "id=" << room.id
           << ";name=" << room.name
           << ";players=" << room.playerKeys.size()
           << ";status=";

        switch (room.status) {
            case RoomStatus::WAITING:  ss << "WAITING";  break;
            case RoomStatus::IN_GAME:  ss << "IN_GAME";  break;
            case RoomStatus::FINISHED: ss << "FINISHED"; break;
        }
        ss << "\n";

        auto s = ss.str();
        sendto(sockfd, s.c_str(), s.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
    }
}

// CREATE_ROOM
// Klient → server:  ID;CREATE_ROOM;<name>
// Server → klient:  ID;CREATE_ROOM_OK;room=<roomId>
// nebo:             ID;ERROR;INVALID_FORMAT;Expected name
void handleCreateRoom(
    const Message& msg,
    RoomsMap& rooms,
    int& nextRoomId,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
) {
    if (msg.rawParams.size() < 1) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Expected name\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    std::string name = msg.rawParams[0];

    Room room;
    room.id     = nextRoomId++;
    room.name   = name;
    room.status = RoomStatus::WAITING;
    room.turn   = Turn::NONE;
    room.board.clear();

    rooms[room.id] = room;

    std::cout << "Created room id=" << room.id
              << " name=" << room.name
              << " max=" << ROOM_CAPACITY << std::endl;

    std::string resp = std::to_string(msg.id) +
                       ";CREATE_ROOM_OK;room=" +
                       std::to_string(room.id) + "\n";

    sendto(sockfd, resp.c_str(), resp.size(), 0,
           reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
}

// JOIN_ROOM
// Klient → server:  ID;JOIN_ROOM;<roomId>
// Server → klient:  ID;JOIN_ROOM_OK;room=<roomId>;players=<count>/<ROOM_CAPACITY>
// nebo:             ID;ERROR;ROOM_NOT_FOUND
//                    ID;ERROR;NOT_LOGGED_IN
//                    ID;ERROR;ROOM_FULL
// Když se místnost naplní:
//    všem:          ID;GAME_START;room=<roomId>;you=<WHITE|BLACK>
//    všem:          ID;GAME_STATE;room=<roomId>;turn=PLAYER1;board=<64 znaků>
void handleJoinRoom(
    const Message& msg,
    const std::string& clientKey,
    RoomsMap& rooms,
    const PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
) {
    if (msg.rawParams.size() < 1) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Missing roomId\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    int roomId = std::stoi(msg.rawParams[0]);

    auto itRoom = rooms.find(roomId);
    if (itRoom == rooms.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;ROOM_NOT_FOUND\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    auto itPlayer = players.find(clientKey);
    if (itPlayer == players.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_LOGGED_IN\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    Room& room = itRoom->second;

    if (room.playerKeys.size() >= ROOM_CAPACITY) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;ROOM_FULL\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    // přidáme klienta, pokud tam ještě není
    if (std::find(room.playerKeys.begin(), room.playerKeys.end(), clientKey) ==
        room.playerKeys.end()) {
        room.playerKeys.push_back(clientKey);
    }

    // odpověď JOIN_ROOM_OK jen volajícímu klientovi
    std::string resp = std::to_string(msg.id) +
                       ";JOIN_ROOM_OK;room=" + std::to_string(room.id) +
                       ";players=" + std::to_string(room.playerKeys.size()) +
                       "/" + std::to_string(ROOM_CAPACITY) + "\n";

    sendto(sockfd, resp.c_str(), resp.size(), 0,
           reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);

    std::cout << "Player " << clientKey
              << " joined room " << room.id << std::endl;

    // pokud je room plná -> spustit hru
    if (room.playerKeys.size() >= ROOM_CAPACITY) {
        room.status = RoomStatus::IN_GAME;
        room.turn   = Turn::PLAYER1;
        room.board  = createInitialBoard();

        // každému hráči pošleme GAME_START (role WHITE/BLACK)
        for (std::size_t i = 0; i < ROOM_CAPACITY; i++) {
            const std::string& pKey = room.playerKeys[i];
            auto pit = players.find(pKey);
            if (pit == players.end()) continue;

            const Player& p = pit->second;
            sockaddr_in pAddr = p.addr;
            socklen_t pLen = sizeof(pAddr);

            std::string role = (i == 0) ? "WHITE" : "BLACK";

            std::string startMsg = std::to_string(msg.id) +
                                   ";GAME_START;room=" + std::to_string(room.id) +
                                   ";you=" + role + "\n";

            sendto(sockfd, startMsg.c_str(), startMsg.size(), 0,
                   reinterpret_cast<const sockaddr*>(&pAddr), pLen);
        }

        // a hned po startu pošleme všem i GAME_STATE s deskou
        broadcastGameState(msg.id, room, players, sockfd);

        std::cout << "Room " << room.id << " now IN_GAME, "
                  << "players: " << room.playerKeys[0]
                  << " (WHITE), " << room.playerKeys[1] << " (BLACK)"
                  << std::endl;
    }
}

// MOVE
// Klient → server:  ID;MOVE;<roomId>;<fromRow>;<fromCol>;<toRow>;<toCol>
// Server → klient:  (při chybě) ID;ERROR;... (NOT_IN_ROOM, NOT_YOUR_TURN, OUT_OF_BOARD, ...)
// Po validním tahu: všem v room: ID;GAME_STATE;room=<roomId>;turn=<...>;board=<64 znaků>
void handleMove(
    const Message& msg,
    const std::string& clientKey,
    RoomsMap& rooms,
    const PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
) {
    if (msg.rawParams.size() < 5) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Expected roomId, fromRow, fromCol, toRow, toCol\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    int roomId   = std::stoi(msg.rawParams[0]);
    int fromRow  = std::stoi(msg.rawParams[1]);
    int fromCol  = std::stoi(msg.rawParams[2]);
    int toRow    = std::stoi(msg.rawParams[3]);
    int toCol    = std::stoi(msg.rawParams[4]);

    auto itRoom = rooms.find(roomId);
    if (itRoom == rooms.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;ROOM_NOT_FOUND\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    Room& room = itRoom->second;

    if (room.status != RoomStatus::IN_GAME) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;ROOM_NOT_IN_GAME\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    // najdeme index hráče v room
    auto itKey = std::find(room.playerKeys.begin(), room.playerKeys.end(), clientKey);
    if (itKey == room.playerKeys.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_IN_ROOM\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    std::size_t playerIndex = static_cast<std::size_t>(itKey - room.playerKeys.begin());

    // kontrola, jestli je na tahu
    if ((room.turn == Turn::PLAYER1 && playerIndex != 0) ||
        (room.turn == Turn::PLAYER2 && playerIndex != 1)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_YOUR_TURN\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    // kontrola rozsahu
    auto inRange = [](int v) { return v >= 0 && v < BOARD_SIZE; };
    if (!inRange(fromRow) || !inRange(fromCol) ||
        !inRange(toRow)   || !inRange(toCol)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;OUT_OF_BOARD\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    char pieceFrom = getPiece(room, fromRow, fromCol);
    char pieceTo   = getPiece(room, toRow, toCol);

    if (pieceFrom == '.') {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NO_PIECE\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    // určíme barvu hráče: 0 = WHITE (w), 1 = BLACK (b)
    char expected = (playerIndex == 0) ? 'w' : 'b';
    if (pieceFrom != expected) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_YOUR_PIECE\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    if (pieceTo != '.') {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;DEST_NOT_EMPTY\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    // Zjednodušené pravidlo: diagonální krok o 1 pole, bez braní
    int dRow = toRow - fromRow;
    int dCol = toCol - fromCol;
    if (std::abs(dRow) != 1 || std::abs(dCol) != 1) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_MOVE_SIMPLE\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    // směr pohybu podle barvy (white nahoru, black dolů)
    if (expected == 'w' && dRow != -1) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_DIRECTION\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }
    if (expected == 'b' && dRow != 1) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_DIRECTION\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    // provedeme tah
    setPiece(room, toRow, toCol, pieceFrom);
    setPiece(room, fromRow, fromCol, '.');

    // přepneme tah
    room.turn = (room.turn == Turn::PLAYER1) ? Turn::PLAYER2 : Turn::PLAYER1;

    // broadcast nové GAME_STATE všem hráčům v místnosti
    broadcastGameState(msg.id, room, players, sockfd);
}
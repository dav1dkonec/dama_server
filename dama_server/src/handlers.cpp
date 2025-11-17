#include "handlers.hpp"

#include <iostream>
#include <sstream>
#include <arpa/inet.h> // sendto už je v sys/socket.h, ale to míváme přes main

// LOGIN
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

// LIST_ROOMS – pro každou room pošleme jednu zprávu ROOM
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
            case RoomStatus::WAITING: ss << "WAITING"; break;
            case RoomStatus::IN_GAME: ss << "IN_GAME"; break;
            case RoomStatus::FINISHED: ss << "FINISHED"; break;
        }
        ss << "\n";

        auto s = ss.str();
        sendto(sockfd, s.c_str(), s.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
    }
}

// CREATE_ROOM: ID;CREATE_ROOM;<name>
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
    room.id         = nextRoomId++;
    room.name       = name;
    room.status     = RoomStatus::WAITING;

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

// JOIN_ROOM: ID;JOIN_ROOM;<roomId>
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
        room.turn = Turn::PLAYER1;

        for (std::size_t i = 0; i < ROOM_CAPACITY; i++) {
            const std::string& pKey = room.playerKeys[i];
            auto pit = players.find(pKey);
            if (pit == players.end()) continue;

            const Player& p = pit->second;
            sockaddr_in pAddr = p.addr;
            socklen_t pLen = sizeof(pAddr);

            std::string role = (i == 0) ? "WHITE" : "BLACK";

            std::string resp = std::to_string(msg.id) +
                                ";GAME_START;room=" + std::to_string(room.id) +
                                    ";you=" + role + "\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                reinterpret_cast<const sockaddr*>(&pAddr), pLen);
        }

        std::cout << "Room " << room.id << " now IN_GAME, "
                  << "players: " << room.playerKeys[0]
                  << " (WHITE), " << room.playerKeys[1] << " (BLACK)"
                  << std::endl;
    }
}
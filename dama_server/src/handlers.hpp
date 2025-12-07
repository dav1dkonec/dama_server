#pragma once

#include <map>
#include <netinet/in.h>

#include "protocol.hpp"
#include "models.hpp"

// Pro zkrácení zápisu

using PlayersMap = std::map<std::string, Player>; // clientKey -> Player
using RoomsMap   = std::map<int, Room>;           // roomId   -> Room

// Jednotlivé "handler" funkce

void handleLogin(
    const Message& msg,
    const std::string& clientKey,
    PlayersMap& players,
    int& nextPlayerId,
    const ServerLimits& limits,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
);

void handlePing(
    const Message& msg,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
);

void handleListRooms(
    const Message& msg,
    const RoomsMap& rooms,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
);

void handleCreateRoom(
    const Message& msg,
    RoomsMap& rooms,
    int& nextRoomId,
    const ServerLimits& limits,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
);

void handleJoinRoom(
    const Message& msg,
    const std::string& clientKey,
    RoomsMap& rooms,
    const PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
);

void handleMove(
    const Message& msg,
    const std::string& clientKey,
    RoomsMap& rooms,
    PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
);

void handleLeaveRoom(
    const Message& msg,
    const std::string& clientKey,
    RoomsMap& rooms,
    const PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
);

void handleLegalMoves(
    const Message& msg,
    const std::string& clientKey,
    RoomsMap& rooms,
    const PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
);

void checkTimeouts(
    PlayersMap& players,
    RoomsMap& rooms,
    int heartbeatTimeoutMs,
    int turnTimeoutMs,
    int sockfd
);

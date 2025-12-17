#pragma once

#include <map>
#include <netinet/in.h>

#include "protocol.hpp"
#include "models.hpp"

// Pro zkrácení zápisu

using PlayersMap = std::map<std::string, Player>; // clientKey -> Player
using RoomsMap   = std::map<int, Room>;           // roomId   -> Room

void sendConfig(Player& player, int sockfd, int turnTimeoutMs);
void sendGameStateToPlayer(int msgId, const Room& room, const Player& p, int sockfd, int turnTimeoutMs);
void pauseRoom(Room& room, PlayersMap& players, int sockfd, int reconnectWindowMs, const std::string& offenderKey = "");

// Jednotlivé "handler" funkce

void handleLogin(
    const Message& msg,
    const std::string& clientKey,
    PlayersMap& players,
    int& nextPlayerId,
    const ServerLimits& limits,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen,
    int turnTimeoutMs,
    int reconnectWindowMs,
    std::map<std::string, std::string>& tokenToKey
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
    socklen_t clientLen,
    ServerLimits& mutableLimits
);

void handleJoinRoom(
    const Message& msg,
    const std::string& clientKey,
    RoomsMap& rooms,
    const PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen,
    int turnTimeoutMs
);

void handleMove(
    const Message& msg,
    const std::string& clientKey,
    RoomsMap& rooms,
    PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen,
    int turnTimeoutMs
);

void handleLeaveRoom(
    const Message& msg,
    const std::string& clientKey,
    RoomsMap& rooms,
    const PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen,
    int reconnectWindowMs
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
    int sockfd,
    int reconnectWindowMs,
    std::map<std::string, std::string>& tokenToKey
);

void handleBye(
    const Message& msg,
    const std::string& clientKey,
    PlayersMap& players,
    RoomsMap& rooms,
    std::map<std::string, std::string>& tokenToKey,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
);

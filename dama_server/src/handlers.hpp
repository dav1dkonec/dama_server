#pragma once

#include <map>
#include <netinet/in.h>

#include "protocol.hpp"
#include "models.hpp"

// Pro zkrácení zápisu

using PlayersMap = std::map<std::string, Player>; // token -> Player
using EndpointMap = std::map<std::string, std::string>; // clientKey -> token
using RoomsMap   = std::map<int, Room>;           // roomId   -> Room

void sendConfig(Player& player, int sockfd, int turnTimeoutMs);
void sendGameStateToPlayer(int msgId, const Room& room, const Player& p, int sockfd, int turnTimeoutMs);
void pauseRoom(Room& room, PlayersMap& players, int sockfd, int reconnectWindowMs, int turnTimeoutMs, const std::string& offenderKey = "");
void registerInvalidMessage(const std::string& playerToken, PlayersMap& players, RoomsMap& rooms, int sockfd, const std::string& reason);

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
    EndpointMap& endpointToToken
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
    const std::string& playerToken,
    RoomsMap& rooms,
    PlayersMap& players,
    int& nextRoomId,
    const ServerLimits& limits,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen,
    ServerLimits& mutableLimits
);

void handleJoinRoom(
    const Message& msg,
    const std::string& playerToken,
    RoomsMap& rooms,
    PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen,
    int turnTimeoutMs
);

void handleMove(
    const Message& msg,
    const std::string& playerToken,
    RoomsMap& rooms,
    PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen,
    int turnTimeoutMs
);

void handleLeaveRoom(
    const Message& msg,
    const std::string& playerToken,
    RoomsMap& rooms,
    PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen,
    int reconnectWindowMs
);

void handleLegalMoves(
    const Message& msg,
    const std::string& playerToken,
    RoomsMap& rooms,
    PlayersMap& players,
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
    EndpointMap& endpointToToken
);

void handleBye(
    const Message& msg,
    const std::string& playerToken,
    PlayersMap& players,
    RoomsMap& rooms,
    EndpointMap& endpointToToken,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
);

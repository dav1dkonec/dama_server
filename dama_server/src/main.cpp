#include <iostream>
#include <map>
#include <cstring>

#include <sys/types.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <unistd.h>

#include "protocol.hpp"
#include "models.hpp"
#include "handlers.hpp"

int main() {
    const int PORT = 5000;

    int sockfd = socket(AF_INET, SOCK_DGRAM, 0);
    if (sockfd < 0) {
        perror("socket");
        return 1;
    }

    sockaddr_in servAddr{};
    servAddr.sin_family = AF_INET;
    servAddr.sin_addr.s_addr = INADDR_ANY;
    servAddr.sin_port = htons(PORT);

    if (bind(sockfd, reinterpret_cast<sockaddr*>(&servAddr),
             sizeof(servAddr)) < 0) {
        perror("bind");
        close(sockfd);
        return 1;
    }

    std::cout << "Dama UDP server running on port " << PORT << std::endl;

    // Stav serveru
    PlayersMap players;
    RoomsMap rooms;
    int nextPlayerId = 1;
    int nextRoomId   = 1;

    char buffer[1024];

    while (true) {
        sockaddr_in clientAddr{};
        socklen_t clientLen = sizeof(clientAddr);

        ssize_t n = recvfrom(sockfd, buffer, sizeof(buffer) - 1, 0,
                             reinterpret_cast<sockaddr*>(&clientAddr), &clientLen);
        if (n < 0) {
            perror("recvfrom");
            continue;
        }

        buffer[n] = '\0';
        std::string line(buffer);
        rtrim(line);

        std::cout << "Received: [" << line << "]" << std::endl;

        Message msg;
        if (!parseMessage(line, msg)) {
            std::cerr << "Invalid message format" << std::endl;
            std::string resp = "0;ERROR;INVALID_FORMAT;Cannot parse message\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<sockaddr*>(&clientAddr), clientLen);
            continue;
        }

        std::string clientKey = addrToKey(clientAddr);

        if (msg.type == "LOGIN") {
            handleLogin(msg, clientKey, players, nextPlayerId,
                        sockfd, clientAddr, clientLen);
        }
        else if (msg.type == "PING") {
            handlePing(msg, sockfd, clientAddr, clientLen);
        }
        else if (msg.type == "LIST_ROOMS") {
            handleListRooms(msg, rooms, sockfd, clientAddr, clientLen);
        }
        else if (msg.type == "CREATE_ROOM") {
            handleCreateRoom(msg, rooms, nextRoomId,
                             sockfd, clientAddr, clientLen);
        }
        else if (msg.type == "JOIN_ROOM") {
            handleJoinRoom(msg, clientKey, rooms, players,
                           sockfd, clientAddr, clientLen);
        }
        else if (msg.type == "MOVE") {
            handleMove(msg, clientKey, rooms, players,
                        sockfd, clientAddr, clientLen);
        }
        else if (msg.type == "LEAVE_ROOM") {
            handleLeaveRoom(msg, clientKey, rooms, players,
                            sockfd, clientAddr, clientLen);
        }
        else {
            std::string resp = std::to_string(msg.id) +
                               ";ERROR;UNSUPPORTED_TYPE;Not implemented yet\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<sockaddr*>(&clientAddr), clientLen);
        }
    }

    close(sockfd);
    return 0;
}
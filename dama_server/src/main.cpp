#include <iostream>
#include <map>
#include <cstring>
#include <string>
#include <chrono>

#include <sys/types.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>

#include "protocol.hpp"
#include "models.hpp"
#include "handlers.hpp"

int main(int argc, char* argv[]) {
    int port = 5000;
    std::string host = "0.0.0.0";
    ServerLimits limits;
    int timeoutMs = 15000;
    int timeoutGrace = 2;
    int turnTimeoutMs = 60000;
    const int timeoutCheckIntervalMs = 500;

    // jednoduché zpracování argumentů --players X --rooms Y --host IP --port port --timeout-ms --tunr-timeout-ms --timeout-grace
    for (int i = 1; i < argc; ++i) {
        std::string arg = argv[i];
        if (arg == "--players" && i + 1 < argc) {
            try {
                limits.maxPlayers = std::stoi(argv[++i]);
            } catch (...) {
                std::cerr << "Invalid argument for --players" << std::endl;
                return 1;
            }
        } else if (arg == "--rooms" && i + 1 < argc) {
            try {
                limits.maxRooms = std::stoi(argv[++i]);
            } catch (...) {
                std::cerr << "Invalid argument for --rooms" << std::endl;
                return 1;
            }
        } else if (arg == "--port" && i + 1 < argc) {
            try {
                port = std::stoi(argv[++i]);
                if (port <= 0 || port > 65535) {
                    std::cerr << "Port must be in range 1-65535" << std::endl;
                    return 1;
                }
            } catch (...) {
                std::cerr << "Invalid argument for --port" << std::endl;
                return 1;
            }
        } else if (arg == "--host" && i + 1 < argc) {
            host = argv[++i];
        } else if (arg == "--timeout-ms" && i + 1 < argc) {
            try {
                timeoutMs = std::stoi(argv[++i]);
                if (timeoutMs <= 0) {
                    std::cerr << "Timeout must be positive" << std::endl;
                    return 1;
                }
            } catch (...) {
                std::cerr << "Invalid argument for --timeout-ms" << std::endl;
                return 1;
            }
        } else if (arg == "--timeout-grace" && i + 1 < argc) {
            try {
                timeoutGrace = std::stoi(argv[++i]);
                if (timeoutGrace < 1) {
                    std::cerr << "Grace factor must be >= 1" << std::endl;
                    return 1;
                }
            } catch (...) {
                std::cerr << "Invalid argument for --timeout-grace" << std::endl;
                return 1;
            }
        } else if (arg == "--turn-timeout-ms" && i + 1 < argc) {
            try {
                turnTimeoutMs = std::stoi(argv[++i]);
                if (turnTimeoutMs <= 0) {
                    std::cerr << "Turn timeout must be positive" << std::endl;
                    return 1;
                }
            } catch (...) {
                std::cerr << "Invalid argument for --turn-timeout-ms" << std::endl;
                return 1;
            }
        }
    }

    int sockfd = socket(AF_INET, SOCK_DGRAM, 0);
    if (sockfd < 0) {
        perror("socket");
        return 1;
    }

    sockaddr_in servAddr{};
    servAddr.sin_family = AF_INET;
    servAddr.sin_port = htons(port);

    if (host == "0.0.0.0") {
        servAddr.sin_addr.s_addr = INADDR_ANY;
    } else {
        if (inet_pton(AF_INET, host.c_str(), &servAddr.sin_addr) != 1) {
            std::cerr << "Invalid IPv4 address: " << host << std::endl;
            close(sockfd);
            return 1;
        }
    }

    if (bind(sockfd, reinterpret_cast<sockaddr*>(&servAddr),
             sizeof(servAddr)) < 0) {
        perror("bind");
        close(sockfd);
        return 1;
    }

    std::cout << "Dama UDP server running on " << host << ":" << port << std::endl;

    // Stav serveru
    PlayersMap players;
    RoomsMap rooms;
    int nextPlayerId = 1;
    int nextRoomId   = 1;

    char buffer[1024];
    auto lastTimeoutCheck = std::chrono::steady_clock::now();

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

        if (line.size() > 1024) {
            std::string resp = "0;ERROR;INVALID_FORMAT;Message too long\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<sockaddr*>(&clientAddr), clientLen);
            continue;
        }

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
        auto now = std::chrono::steady_clock::now();

        auto itPlayerSeen = players.find(clientKey);
        if (itPlayerSeen != players.end()) {
            itPlayerSeen->second.lastSeen = now;
            itPlayerSeen->second.connected = true;
            itPlayerSeen->second.addr = clientAddr;
        }

        if (msg.type == "LOGIN") {
            handleLogin(msg, clientKey, players, nextPlayerId, limits,
                        sockfd, clientAddr, clientLen);
        }
        else if (msg.type == "PING") {
            handlePing(msg, sockfd, clientAddr, clientLen);
        }
        else if (msg.type == "LIST_ROOMS") {
            handleListRooms(msg, rooms, sockfd, clientAddr, clientLen);
        }
        else if (msg.type == "CREATE_ROOM") {
            handleCreateRoom(msg, rooms, nextRoomId, limits,
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
        else if (msg.type == "LEGAL_MOVES") {
            handleLegalMoves(msg, clientKey, rooms, players,
                             sockfd, clientAddr, clientLen);
        }
        else {
            std::string resp = std::to_string(msg.id) +
                               ";ERROR;UNSUPPORTED_TYPE;Nepodporovaný typ zprávy\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<sockaddr*>(&clientAddr), clientLen);
        }

        if (std::chrono::duration_cast<std::chrono::milliseconds>(
                now - lastTimeoutCheck).count() > timeoutCheckIntervalMs) {
            int effectiveHeartbeatMs = timeoutMs * timeoutGrace;
            checkTimeouts(players, rooms, effectiveHeartbeatMs, turnTimeoutMs, sockfd);
            lastTimeoutCheck = now;
        }
    }

    close(sockfd);
    return 0;
}

#include <iostream>
#include <map>
#include <cstring>
#include <string>
#include <chrono>
#include <thread>
#include <algorithm>

#include <sys/types.h>
#include <sys/socket.h>
#include <netinet/in.h>
#include <arpa/inet.h>
#include <unistd.h>
#include <errno.h>

#include "protocol.hpp"
#include "models.hpp"
#include "handlers.hpp"

int main(int argc, char* argv[]) {
    int port = 5000;
    std::string host = "0.0.0.0";
    ServerLimits limits;
    int timeoutMs = 20000;
    int timeoutGrace = 1;
    int turnTimeoutMs = 60000;
    const int timeoutCheckIntervalMs = 500;
    int reconnectWindowMs = 60000;

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
        } else if (arg == "--reconnect-window-ms" && i + 1 < argc) {
            try {
                reconnectWindowMs = std::stoi(argv[++i]);
                if (reconnectWindowMs <= 0) {
                    std::cerr << "Reconnect window must be positive" << std::endl;
                    return 1;
                }
            } catch (...) {
                std::cerr << "Invalid argument for --reconnect-window-ms" << std::endl;
                return 1;
            }
        }
    }

    int sockfd = socket(AF_INET, SOCK_DGRAM, 0);
    if (sockfd < 0) {
        perror("socket");
        return 1;
    }
    int reuse = 1;
    if (setsockopt(sockfd, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse)) < 0) {
        perror("setsockopt SO_REUSEADDR");
    }
    if (setsockopt(sockfd, SOL_SOCKET, SO_REUSEPORT, &reuse, sizeof(reuse)) < 0) {
        perror("setsockopt SO_REUSEPORT");
    }
    timeval recvTimeout{};
    recvTimeout.tv_sec = timeoutCheckIntervalMs / 1000;
    recvTimeout.tv_usec = (timeoutCheckIntervalMs % 1000) * 1000;
    if (setsockopt(sockfd, SOL_SOCKET, SO_RCVTIMEO, &recvTimeout, sizeof(recvTimeout)) < 0) {
        perror("setsockopt SO_RCVTIMEO");
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
    EndpointMap endpointToToken; // clientKey -> token
    RoomsMap rooms;
    int nextPlayerId = 1;
    int nextRoomId   = 1;

    // Discovery socket (UDP, fixed port 9999).
    int discSock = socket(AF_INET, SOCK_DGRAM, 0);
    bool discoveryActive = true;
    if (discSock < 0) {
        perror("socket discovery");
        discoveryActive = false;
    } else {
        if (setsockopt(discSock, SOL_SOCKET, SO_REUSEADDR, &reuse, sizeof(reuse)) < 0) {
            perror("setsockopt discovery SO_REUSEADDR");
        }
        if (setsockopt(discSock, SOL_SOCKET, SO_REUSEPORT, &reuse, sizeof(reuse)) < 0) {
            perror("setsockopt discovery SO_REUSEPORT");
        }
        sockaddr_in discAddr{};
        discAddr.sin_family = AF_INET;
        discAddr.sin_port = htons(9999);
        discAddr.sin_addr.s_addr = INADDR_ANY;
        if (bind(discSock, reinterpret_cast<sockaddr*>(&discAddr), sizeof(discAddr)) < 0) {
            perror("bind discovery");
            discoveryActive = false;
        }
    }

    std::thread discoveryThread;
    if (discoveryActive) {
        discoveryThread = std::thread([&]() {
            char buf[256];
            while (true) {
                sockaddr_in cli{};
                socklen_t clen = sizeof(cli);
                ssize_t n = recvfrom(discSock, buf, sizeof(buf) - 1, 0,
                                     reinterpret_cast<sockaddr*>(&cli), &clen);
                if (n <= 0) {
                    continue;
                }
                buf[n] = '\0';
                std::string line(buf);
                rtrim(line);
                if (line == "DISCOVER") {
                    std::string respHost = host;
                    if (host == "0.0.0.0") {
                        // vrací konkrétní IP, kterou klient použil k dotazu
                        auto key = addrToKey(cli);
                        auto pos = key.find(':');
                        if (pos != std::string::npos) {
                            respHost = key.substr(0, pos);
                        } else {
                            respHost = key;
                        }
                    }
                    std::string resp = "0;ENDPOINT;host=" + respHost + ";port=" + std::to_string(port) + "\n";
                    sendto(discSock, resp.c_str(), resp.size(), 0,
                           reinterpret_cast<sockaddr*>(&cli), clen);
                    std::cout << "[DISCOVERY] Reply to " << addrToKey(cli) << " endpoint=" << respHost << ":" << port << std::endl;
                }
            }
        });
    } else {
        std::cerr << "[WARN] Discovery socket not started; port busy. Manual host/port required." << std::endl;
    }
    char buffer[1024];
    auto lastTimeoutCheck = std::chrono::steady_clock::now();

    while (true) {
        sockaddr_in clientAddr{};
        socklen_t clientLen = sizeof(clientAddr);

        ssize_t n = recvfrom(sockfd, buffer, sizeof(buffer) - 1, 0,
                             reinterpret_cast<sockaddr*>(&clientAddr), &clientLen);
        if (n < 0) {
            if (errno == EAGAIN || errno == EWOULDBLOCK) {
                auto nowTimeout = std::chrono::steady_clock::now();
                if (std::chrono::duration_cast<std::chrono::milliseconds>(
                        nowTimeout - lastTimeoutCheck).count() > timeoutCheckIntervalMs) {
                    int effectiveHeartbeatMs = timeoutMs * timeoutGrace;
                    int pauseThresholdMs = std::min(12000, effectiveHeartbeatMs);
                    checkTimeouts(players, rooms, effectiveHeartbeatMs, pauseThresholdMs, turnTimeoutMs,
                                  sockfd, reconnectWindowMs, endpointToToken);
                    lastTimeoutCheck = nowTimeout;
                }
            } else {
                perror("recvfrom");
            }
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
            std::string invalidKey = addrToKey(clientAddr);
            auto itInvalidEndpoint = endpointToToken.find(invalidKey);
            if (itInvalidEndpoint != endpointToToken.end()) {
                registerInvalidMessage(itInvalidEndpoint->second, players, rooms, sockfd, "INVALID_FORMAT");
            }
            continue;
        }

        std::string clientKey = addrToKey(clientAddr);
        std::string playerToken;
        auto now = std::chrono::steady_clock::now();

        auto itEndpoint = endpointToToken.find(clientKey);
        if (itEndpoint != endpointToToken.end()) {
            playerToken = itEndpoint->second;
            auto itPlayerSeen = players.find(playerToken);
            if (itPlayerSeen != players.end()) {
                itPlayerSeen->second.lastSeen = now;
                if (!itPlayerSeen->second.paused) {
                    itPlayerSeen->second.connected = true;
                    itPlayerSeen->second.addr = clientAddr;
                }
            } else {
                endpointToToken.erase(itEndpoint);
                playerToken.clear();
            }
        }

        auto sendNotLoggedIn = [&]() {
            std::string resp = std::to_string(msg.id) + ";ERROR;NOT_LOGGED_IN\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<sockaddr*>(&clientAddr), clientLen);
        };

        if (msg.type == "LOGIN") {
            handleLogin(msg, clientKey, players, nextPlayerId, limits,
                        sockfd, clientAddr, clientLen, turnTimeoutMs, reconnectWindowMs, endpointToToken);
        }
        else if (msg.type == "PING") {
            if (!playerToken.empty()) {
                std::cout << "[PING] token=" << playerToken
                          << " addr=" << clientKey << std::endl;
            }
            handlePing(msg, sockfd, clientAddr, clientLen);
        }
        else if (msg.type == "LIST_ROOMS") {
            if (playerToken.empty()) {
                sendNotLoggedIn();
            } else {
                handleListRooms(msg, rooms, sockfd, clientAddr, clientLen);
            }
        }
        else if (msg.type == "CREATE_ROOM") {
            if (playerToken.empty()) {
                sendNotLoggedIn();
            } else {
                handleCreateRoom(msg, playerToken, rooms, players, nextRoomId, limits,
                             sockfd, clientAddr, clientLen, limits);
            }
        }
        else if (msg.type == "JOIN_ROOM") {
            if (playerToken.empty()) {
                sendNotLoggedIn();
            } else {
                handleJoinRoom(msg, playerToken, rooms, players,
                               sockfd, clientAddr, clientLen, turnTimeoutMs);
            }
        }
        else if (msg.type == "MOVE") {
            if (playerToken.empty()) {
                sendNotLoggedIn();
            } else {
                handleMove(msg, playerToken, rooms, players,
                            sockfd, clientAddr, clientLen, turnTimeoutMs);
            }
        }
        else if (msg.type == "LEAVE_ROOM") {
            if (playerToken.empty()) {
                sendNotLoggedIn();
            } else {
                handleLeaveRoom(msg, playerToken, rooms, players,
                                sockfd, clientAddr, clientLen, reconnectWindowMs);
            }
        }
        else if (msg.type == "LEGAL_MOVES") {
            if (playerToken.empty()) {
                sendNotLoggedIn();
            } else {
                handleLegalMoves(msg, playerToken, rooms, players,
                                 sockfd, clientAddr, clientLen);
            }
        }
        else if (msg.type == "BYE") {
            if (playerToken.empty()) {
                sendNotLoggedIn();
            } else {
                handleBye(msg, playerToken, players, rooms, endpointToToken, sockfd, clientAddr, clientLen);
                endpointToToken.erase(clientKey);
            }
            continue;
        }
        else if (msg.type == "CONFIG_ACK") {
            auto pit = players.find(playerToken);
            if (pit != players.end()) {
                pit->second.configAcked = true;
                std::cout << "[INFO] CONFIG_ACK from " << clientKey << std::endl;
            }
        }
        else if (msg.type == "RECONNECT") {
            // handled in receive loop níže
        }
        else {
            std::string resp = std::to_string(msg.id) +
                               ";ERROR;UNSUPPORTED_TYPE;Nepodporovaný typ zprávy\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<sockaddr*>(&clientAddr), clientLen);
            if (!playerToken.empty()) {
                registerInvalidMessage(playerToken, players, rooms, sockfd, "UNSUPPORTED_TYPE");
            }
        }

        if (std::chrono::duration_cast<std::chrono::milliseconds>(
                now - lastTimeoutCheck).count() > timeoutCheckIntervalMs) {
            int effectiveHeartbeatMs = timeoutMs * timeoutGrace;
            int pauseThresholdMs = std::min(12000, effectiveHeartbeatMs);
            checkTimeouts(players, rooms, effectiveHeartbeatMs, pauseThresholdMs, turnTimeoutMs, sockfd, reconnectWindowMs, endpointToToken);
            lastTimeoutCheck = now;
        }

        auto pit = players.find(playerToken);
        if (pit != players.end() && !pit->second.configAcked) {
            auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - pit->second.lastConfigSent).count();
            if (pit->second.lastConfigSent == std::chrono::steady_clock::time_point{} || elapsed > 3000) {
                sendConfig(pit->second, sockfd, pit->second.turnTimeoutMs);
                std::cout << "[INFO] RESEND_CONFIG to " << clientKey
                          << " timeoutMs=" << pit->second.turnTimeoutMs << std::endl;
            }
        }

        // RECONNECT handling: if message type is RECONNECT, validate token
        if (msg.type == "RECONNECT") {
            if (msg.rawParams.empty()) {
                std::string resp = std::to_string(msg.id) + ";ERROR;INVALID_FORMAT;Missing token\n";
                sendto(sockfd, resp.c_str(), resp.size(), 0,
                       reinterpret_cast<sockaddr*>(&clientAddr), clientLen);
                continue;
            }
            std::string token = msg.rawParams[0];
            auto pitToken = players.find(token);
            if (pitToken == players.end()) {
                std::string resp = std::to_string(msg.id) + ";ERROR;TOKEN_NOT_FOUND\n";
                sendto(sockfd, resp.c_str(), resp.size(), 0,
                       reinterpret_cast<sockaddr*>(&clientAddr), clientLen);
                continue;
            }
            Player& p = pitToken->second;
            auto nowTs = std::chrono::steady_clock::now();
            if (p.resumeDeadline != std::chrono::steady_clock::time_point{} &&
                nowTs > p.resumeDeadline) {
                std::string resp = std::to_string(msg.id) + ";ERROR;TOKEN_EXPIRED\n";
                sendto(sockfd, resp.c_str(), resp.size(), 0,
                       reinterpret_cast<sockaddr*>(&clientAddr), clientLen);
                continue;
            }

            p.addr = clientAddr;
            p.connected = true;
            p.lastSeen = nowTs;
            p.paused = false;
            p.resumeDeadline = std::chrono::steady_clock::time_point{};
            for (auto it = endpointToToken.begin(); it != endpointToToken.end();) {
                if (it->second == token) {
                    it = endpointToToken.erase(it);
                } else {
                    ++it;
                }
            }
            endpointToToken[clientKey] = token;
            std::string resp = std::to_string(msg.id) + ";RECONNECT_OK\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<sockaddr*>(&clientAddr), clientLen);
            std::cout << "[INFO] RECONNECT_OK token=" << token << " key=" << clientKey << std::endl;
            // pošle poslední game state jen pokud jsou oba hráči připojeni (jinak zůstává pauza)
            auto nowSys = std::chrono::system_clock::now();
            for (auto& [roomId, room] : rooms) {
                auto it = std::find(room.playerKeys.begin(), room.playerKeys.end(), token);
                if (it == room.playerKeys.end()) continue;
                if (room.status != RoomStatus::IN_GAME) continue;

                bool allReady = true;
                std::chrono::milliseconds::rep resumeByEpochMs = 0;
                for (const auto& pKey : room.playerKeys) {
                    auto pit = players.find(pKey);
                    if (pit == players.end()) {
                        allReady = false;
                        continue;
                    }
                    const Player& rp = pit->second;
                    if (rp.paused || !rp.connected) {
                        allReady = false;
                    }
                    if (rp.paused && rp.resumeDeadline != std::chrono::steady_clock::time_point{}) {
                        auto remaining = rp.resumeDeadline - nowTs;
                        if (remaining > std::chrono::milliseconds::zero()) {
                            auto candidate = nowSys + remaining;
                            auto ms = std::chrono::duration_cast<std::chrono::milliseconds>(
                                candidate.time_since_epoch()).count();
                            resumeByEpochMs = std::max(resumeByEpochMs, ms);
                        }
                    }
                }

                if (allReady) {
                    if (room.remainingTurnMs >= 0) {
                        room.lastTurnAt = nowTs - std::chrono::milliseconds(turnTimeoutMs - room.remainingTurnMs);
                        room.remainingTurnMs = -1;
                    } else if (room.lastTurnAt == std::chrono::steady_clock::time_point{}) {
                        room.lastTurnAt = nowTs;
                    }
                    for (const auto& pKey : room.playerKeys) {
                        auto pit = players.find(pKey);
                        if (pit == players.end()) continue;
                        sendGameStateToPlayer(msg.id, room, pit->second, sockfd, turnTimeoutMs);
                    }
                } else {
                    if (resumeByEpochMs == 0) {
                        resumeByEpochMs = std::chrono::duration_cast<std::chrono::milliseconds>(
                            (nowSys + std::chrono::milliseconds(reconnectWindowMs)).time_since_epoch()).count();
                    }
                    std::string pauseMsg = "0;GAME_PAUSED;room=" + std::to_string(room.id) +
                                           ";resumeBy=" + std::to_string(resumeByEpochMs) + "\n";
                    sendto(sockfd, pauseMsg.c_str(), pauseMsg.size(), 0,
                           reinterpret_cast<sockaddr*>(&clientAddr), clientLen);
                }
            }
            continue;
        }
    }

    discoveryThread.detach();
    close(sockfd);
    close(discSock);
    return 0;
}

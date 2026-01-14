#include "handlers.hpp"
#include "models.hpp"
#include <iostream>
#include <sstream>
#include <algorithm>
#include <cmath>
#include <cstddef>
#include <chrono>
#include <random>
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

bool isDarkSquare(int row, int col) {
    return ((row + col) % 2) == 1;
}

PieceColor pieceColor(char piece) {
    if (piece == 'w' || piece == 'W') return PieceColor::WHITE;
    if (piece == 'b' || piece == 'B') return PieceColor::BLACK;
    return PieceColor::NONE;
}

bool isKing(char piece) {
    return piece == 'W' || piece == 'B';
}

std::vector<std::pair<int, int>> moveDirections(char piece) {
    // pěšci jdou jen dopředu, dáma oběma směry
    if (isKing(piece)) {
        return {{-1, -1}, {-1, 1}, {1, -1}, {1, 1}};
    }
    if (piece == 'w') {
        return {{-1, -1}, {-1, 1}};
    }
    return {{1, -1}, {1, 1}}; // tahy pro černého
}

bool inBoard(int row, int col) {
    return row >= 0 && row < BOARD_SIZE && col >= 0 && col < BOARD_SIZE;
}

bool hasInvalidDelims(const std::string& s) {
    return s.find(';') != std::string::npos || s.find('=') != std::string::npos;
}

bool exceedsLimit(const std::string& s, std::size_t maxLen) {
    return s.size() > maxLen;
}

bool parseInt(const std::string& s, int& out) {
    try {
        out = std::stoi(s);
        return true;
    } catch (...) {
        return false;
    }
}

bool canCaptureFrom(const Room& room, int row, int col, char piece) {
    auto dirs = moveDirections(piece);
    PieceColor myColor = pieceColor(piece);
    PieceColor enemy   = (myColor == PieceColor::WHITE) ? PieceColor::BLACK : PieceColor::WHITE;

    for (auto [dr, dc] : dirs) {
        if (!isKing(piece)) {
            int midRow = row + dr;
            int midCol = col + dc;
            int dstRow = row + 2 * dr;
            int dstCol = col + 2 * dc;

            if (!inBoard(dstRow, dstCol) || !isDarkSquare(dstRow, dstCol)) continue;
            char middle = getPiece(room, midRow, midCol);
            char dest   = getPiece(room, dstRow, dstCol);

            if (dest != '.') continue;
            if (pieceColor(middle) != enemy) continue;
            return true;
        } else {
            int r = row + dr;
            int c = col + dc;
            bool enemyFound = false;

            while (inBoard(r, c) && isDarkSquare(r, c)) {
                char cur = getPiece(room, r, c);
                if (cur == '.') {
                    if (enemyFound) {
                        return true; // našli jsme nepřítele a za ním volné pole
                    }
                } else if (pieceColor(cur) == myColor) {
                    break; // blokuje vlastní figura
                } else { // nepřítel
                    if (enemyFound) break; // druhá figurka, konec
                    enemyFound = true;
                }
                r += dr;
                c += dc;
            }
        }
    }
    return false;
}

bool playerHasAnyCapture(const Room& room, PieceColor color) {
    for (int r = 0; r < BOARD_SIZE; ++r) {
        for (int c = 0; c < BOARD_SIZE; ++c) {
            char p = getPiece(room, r, c);
            if (pieceColor(p) != color) continue;
            if (canCaptureFrom(room, r, c, p)) return true;
        }
    }
    return false;
}

bool hasAnyPiece(const Room& room, PieceColor color) {
    for (int r = 0; r < BOARD_SIZE; ++r) {
        for (int c = 0; c < BOARD_SIZE; ++c) {
            if (pieceColor(getPiece(room, r, c)) == color) {
                return true;
            }
        }
    }
    return false;
}

bool playerHasAnySimpleMove(const Room& room, PieceColor color) {
    for (int r = 0; r < BOARD_SIZE; ++r) {
        for (int c = 0; c < BOARD_SIZE; ++c) {
            char p = getPiece(room, r, c);
            if (pieceColor(p) != color) continue;

            for (auto [dr, dc] : moveDirections(p)) {
                int nr = r + dr;
                int nc = c + dc;
                if (!inBoard(nr, nc) || !isDarkSquare(nr, nc)) continue;
                if (getPiece(room, nr, nc) == '.') {
                    return true;
                }
            }
        }
    }
    return false;
}

bool playerHasAnyMove(const Room& room, PieceColor color) {
    if (playerHasAnyCapture(room, color)) return true;
    return playerHasAnySimpleMove(room, color);
}

bool roomHasPausedPlayer(const Room& room, const PlayersMap& players) {
    for (const auto& key : room.playerKeys) {
        auto pit = players.find(key);
        if (pit == players.end()) {
            return true;
        }
        if (pit->second.paused || !pit->second.connected) {
            return true;
        }
    }
    return false;
}

void sendGameEnd(int msgId, Room& room, const PlayersMap& players, int sockfd, const std::string& reason, const std::string& winnerOverride = "NONE") {
    room.status = RoomStatus::FINISHED;
    room.turn   = Turn::NONE;
    room.captureLock.reset();

    std::string winner = winnerOverride;
    if (winner == "NONE") {
        if (reason.find("WHITE_WIN") != std::string::npos) {
            winner = "WHITE";
        } else if (reason.find("BLACK_WIN") != std::string::npos) {
            winner = "BLACK";
        }
    }

    for (const auto& pKey : room.playerKeys) {
        auto pit = players.find(pKey);
        if (pit == players.end()) continue;

        const Player& p = pit->second;
        sockaddr_in pAddr = p.addr;
        socklen_t pLen = sizeof(pAddr);

        std::string resp = std::to_string(msgId) +
                           ";GAME_END;room=" + std::to_string(room.id) +
                           ";reason=" + reason +
                           ";winner=" + winner + "\n";

        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&pAddr), pLen);
    }

    std::cout << "[INFO] GAME_END room=" << room.id
              << " reason=" << reason
              << " winner=" << winner << std::endl;
}

static void resetRoom(Room& room) {
    room.status = RoomStatus::WAITING;
    room.turn = Turn::NONE;
    room.board.clear();
    room.captureLock.reset();
    room.lastTurnAt = std::chrono::steady_clock::time_point{};
    room.remainingTurnMs = -1;
    room.playerKeys.clear();
}

void dropPlayerForInvalid(const std::string& playerToken, PlayersMap& players, RoomsMap& rooms, int sockfd) {
    for (auto& [roomId, room] : rooms) {
        auto it = std::find(room.playerKeys.begin(), room.playerKeys.end(), playerToken);
        if (it == room.playerKeys.end()) continue;

        if (room.status == RoomStatus::IN_GAME) {
            sendGameEnd(0, room, players, sockfd, "OPPONENT_LEFT");
            resetRoom(room);
        } else {
            room.playerKeys.erase(it);
            if (room.playerKeys.empty()) {
                resetRoom(room);
            }
        }
    }
    players.erase(playerToken);
}

std::vector<std::pair<int, int>> kingSimpleMoves(const Room& room, int row, int col) {
    std::vector<std::pair<int, int>> out;
    for (auto [dr, dc] : moveDirections('W')) {
        int r = row + dr;
        int c = col + dc;
        while (inBoard(r, c) && isDarkSquare(r, c)) {
            if (getPiece(room, r, c) != '.') break;
            out.emplace_back(r, c);
            r += dr;
            c += dc;
        }
    }
    return out;
}

std::vector<std::pair<int, int>> kingCaptureMoves(const Room& room, int row, int col, PieceColor myColor) {
    std::vector<std::pair<int, int>> out;
    for (auto [dr, dc] : moveDirections('W')) {
        int r = row + dr;
        int c = col + dc;
        bool enemyFound = false;
        while (inBoard(r, c) && isDarkSquare(r, c)) {
            char cur = getPiece(room, r, c);
            if (cur == '.') {
                if (enemyFound) {
                    out.emplace_back(r, c);
                }
            } else if (pieceColor(cur) == myColor) {
                break;
            } else { // enemy
                if (enemyFound) break;
                enemyFound = true;
            }
            r += dr;
            c += dc;
        }
    }
    return out;
}

std::vector<std::pair<int, int>> manSimpleMoves(const Room& room, int row, int col, bool isWhite) {
    std::vector<std::pair<int, int>> out;
    int dir = isWhite ? -1 : 1;
    for (int dc : {-1, 1}) {
        int nr = row + dir;
        int nc = col + dc;
        if (!inBoard(nr, nc) || !isDarkSquare(nr, nc)) continue;
        if (getPiece(room, nr, nc) == '.') {
            out.emplace_back(nr, nc);
        }
    }
    return out;
}

std::vector<std::pair<int, int>> manCaptureMoves(const Room& room, int row, int col, bool isWhite, PieceColor myColor) {
    std::vector<std::pair<int, int>> out;
    int dir = isWhite ? -1 : 1;
    for (int dc : {-1, 1}) {
        int midRow = row + dir;
        int midCol = col + dc;
        int dstRow = row + 2 * dir;
        int dstCol = col + 2 * dc;
        if (!inBoard(dstRow, dstCol) || !isDarkSquare(dstRow, dstCol)) continue;
        char middle = getPiece(room, midRow, midCol);
        char dest = getPiece(room, dstRow, dstCol);
        if (dest != '.') continue;
        if (pieceColor(middle) == myColor || pieceColor(middle) == PieceColor::NONE) continue;
        out.emplace_back(dstRow, dstCol);
    }
    return out;
}


// Broadcast GAME_STATE to all players in room
// Response: ID;GAME_STATE;room=<roomId>;turn=<PLAYER1|PLAYER2|NONE>;board=<64 chars>
void broadcastGameState(
    int msgId,
    const Room& room,
    const PlayersMap& players,
    int sockfd,
    int turnTimeoutMs
) {
    auto now = std::chrono::steady_clock::now();
    long long remainingMs = turnTimeoutMs;
    if (room.lastTurnAt != std::chrono::steady_clock::time_point{}) {
        auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - room.lastTurnAt).count();
        remainingMs = std::max(0LL, static_cast<long long>(turnTimeoutMs) - elapsed);
    } else if (room.remainingTurnMs >= 0) {
        remainingMs = room.remainingTurnMs;
    }

    for (const auto& pKey : room.playerKeys) {
        auto pit = players.find(pKey);
        if (pit == players.end()) continue;

        const Player& p = pit->second;
        sockaddr_in pAddr = p.addr;
        socklen_t pLen = sizeof(pAddr);

        std::string resp = std::to_string(msgId) +
                           ";GAME_STATE;room=" + std::to_string(room.id) +
                           ";turn=" + turnToString(room.turn) +
                           ";board=" + room.board +
                           ";remainingMs=" + std::to_string(remainingMs);
        if (room.captureLock.has_value()) {
            resp += ";lock=" + std::to_string(room.captureLock->first) + "," + std::to_string(room.captureLock->second);
        }
        resp += "\n";

        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&pAddr), pLen);
    }
}

} // namespace

void sendGameStateToPlayer(int msgId, const Room& room, const Player& p, int sockfd, int turnTimeoutMs)
{
    auto now = std::chrono::steady_clock::now();
    long long remainingMs = turnTimeoutMs;
    if (room.lastTurnAt != std::chrono::steady_clock::time_point{}) {
        auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - room.lastTurnAt).count();
        remainingMs = std::max(0LL, static_cast<long long>(turnTimeoutMs) - elapsed);
    } else if (room.remainingTurnMs >= 0) {
        remainingMs = room.remainingTurnMs;
    }

    sockaddr_in pAddr = p.addr;
    socklen_t pLen = sizeof(pAddr);
    std::string resp = std::to_string(msgId) +
                       ";GAME_STATE;room=" + std::to_string(room.id) +
                       ";turn=" + turnToString(room.turn) +
                       ";board=" + room.board +
                       ";remainingMs=" + std::to_string(remainingMs);
    if (room.captureLock.has_value()) {
        resp += ";lock=" + std::to_string(room.captureLock->first) + "," + std::to_string(room.captureLock->second);
    }
    resp += "\n";
    sendto(sockfd, resp.c_str(), resp.size(), 0,
           reinterpret_cast<const sockaddr*>(&pAddr), pLen);
}

void pauseRoom(Room& room, PlayersMap& players, int sockfd, int reconnectWindowMs, int turnTimeoutMs, const std::string& offenderKey)
{
    room.status = RoomStatus::IN_GAME;
    if (room.lastTurnAt != std::chrono::steady_clock::time_point{}) {
        auto now = std::chrono::steady_clock::now();
        auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - room.lastTurnAt).count();
        room.remainingTurnMs = std::max(0, turnTimeoutMs - static_cast<int>(elapsed));
    }
    room.lastTurnAt = std::chrono::steady_clock::time_point{}; // stop turn timer
    auto now = std::chrono::steady_clock::now();
    auto nowSys = std::chrono::system_clock::now();
    auto resumeByEpochMs = std::chrono::duration_cast<std::chrono::milliseconds>(
        nowSys.time_since_epoch() + std::chrono::milliseconds(reconnectWindowMs)).count();
    for (const auto& key : room.playerKeys) {
        auto it = players.find(key);
        if (it == players.end()) continue;
        Player& p = it->second;
        if (!offenderKey.empty() && offenderKey == key) {
            p.connected = false;
            p.paused = true;
            p.resumeDeadline = now + std::chrono::milliseconds(reconnectWindowMs);
        } else if (!p.connected) {
            p.paused = true;
            p.resumeDeadline = now + std::chrono::milliseconds(reconnectWindowMs);
        }
    }
    for (const auto& key : room.playerKeys) {
        auto it = players.find(key);
        if (it == players.end()) continue;
        const Player& p = it->second;
        if (p.connected) {
            sockaddr_in pAddr = p.addr;
            socklen_t pLen = sizeof(pAddr);
            std::string msg = "0;GAME_PAUSED;room=" + std::to_string(room.id) +
                              ";resumeBy=" + std::to_string(resumeByEpochMs) + "\n";
            sendto(sockfd, msg.c_str(), msg.size(), 0,
                   reinterpret_cast<const sockaddr*>(&pAddr), pLen);
            std::cout << "[INFO] GAME_PAUSED room=" << room.id << " resumeBy=" << p.resumeDeadline.time_since_epoch().count() << std::endl;
        }
    }
}

void sendConfig(Player& player, int sockfd, int turnTimeoutMs)
{
    sockaddr_in pAddr = player.addr;
    socklen_t pLen = sizeof(pAddr);
    std::string msg = "0;CONFIG;turnTimeoutMs=" + std::to_string(turnTimeoutMs) + "\n";
    sendto(sockfd, msg.c_str(), msg.size(), 0,
           reinterpret_cast<const sockaddr*>(&pAddr), pLen);
    player.lastConfigSent = std::chrono::steady_clock::now();
}

// LOGIN
// Klient → server:  ID;LOGIN;<nick>
// Server → klient:  ID;LOGIN_OK;player=<playerId>
//                  nebo ID;ERROR;INVALID_FORMAT;Missing nick
//                  nebo ID;ERROR;INVALID_FORMAT;Invalid chars in nick
//                  nebo ID;ERROR;INVALID_FORMAT;Nick too long
//                  nebo ID;ERROR;SERVER_FULL;Players limit reached
// Příklad: 1;LOGIN;alice -> 1;LOGIN_OK;player=1
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
) {
    if (msg.rawParams.size() < 1) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Missing nick\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    std::string nick = msg.rawParams[0];

    if (hasInvalidDelims(nick)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Invalid chars in nick\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }
    if (exceedsLimit(nick, 64)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Nick too long\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    auto itExisting = endpointToToken.find(clientKey);
    if (itExisting != endpointToToken.end()) {
        auto pit = players.find(itExisting->second);
        if (pit != players.end()) {
            const Player& existing = pit->second;
            if (!existing.nick.empty() && existing.nick != nick) {
                std::string resp = std::to_string(msg.id) +
                                   ";ERROR;ALREADY_LOGGED_IN\n";
                sendto(sockfd, resp.c_str(), resp.size(), 0,
                       reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
                std::cout << "[INFO] LOGIN rejected for " << clientKey
                          << " (nick mismatch)" << std::endl;
                return;
            }
            std::string resp = std::to_string(msg.id) +
                               ";LOGIN_OK;player=" + std::to_string(existing.id) +
                               ";token=" + existing.token + "\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
            sendConfig(pit->second, sockfd, turnTimeoutMs);
            std::cout << "[INFO] LOGIN repeat key=" << clientKey
                      << " player=" << existing.id << std::endl;
            return;
        }
        endpointToToken.erase(itExisting);
    }

    if (players.size() >= static_cast<std::size_t>(limits.maxPlayers)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;SERVER_FULL;Vyčerpán limit hráčů\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    Player p;
    p.id   = nextPlayerId++;
    p.nick = nick;
    p.addr = clientAddr;
    p.connected = true;
    p.lastSeen = std::chrono::steady_clock::now();
    p.configAcked = false;
    p.turnTimeoutMs = turnTimeoutMs;
    {
        static std::mt19937_64 rng{std::random_device{}()};
        uint64_t v = rng();
        std::stringstream ss;
        ss << std::hex << v;
        p.token = ss.str();
    }
    p.tokenExpires = std::chrono::steady_clock::time_point{};
    players[p.token] = p;
    endpointToToken[clientKey] = p.token;

    std::cout << "New player: id=" << p.id
              << " nick=" << p.nick
              << " from " << clientKey << std::endl;

    std::string resp = std::to_string(msg.id) +
                       ";LOGIN_OK;player=" + std::to_string(p.id) +
                       ";token=" + p.token + "\n";

    sendto(sockfd, resp.c_str(), resp.size(), 0,
           reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);

    sendConfig(players[p.token], sockfd, turnTimeoutMs);

    std::cout << "[INFO] LOGIN player=" << p.id
              << " nick=" << p.nick
              << " key=" << clientKey
              << " turnTimeoutMs=" << turnTimeoutMs << std::endl;
}

void registerInvalidMessage(const std::string& playerToken, PlayersMap& players, RoomsMap& rooms, int sockfd, const std::string& reason)
{
    if (playerToken.empty()) return;

    auto pit = players.find(playerToken);
    if (pit == players.end()) return;

    auto& player = pit->second;
    auto now = std::chrono::steady_clock::now();
    if (player.invalidWindowStart == std::chrono::steady_clock::time_point{} ||
        now - player.invalidWindowStart > std::chrono::seconds(30)) {
        player.invalidCount = 0;
        player.invalidWindowStart = now;
    }

    player.invalidCount++;
    std::cout << "[WARN] INVALID_MESSAGE token=" << playerToken
              << " count=" << player.invalidCount
              << " reason=" << reason << std::endl;

    if (player.invalidCount >= 3) {
        std::cout << "[WARN] DROP_PLAYER token=" << playerToken << " invalid messages limit reached" << std::endl;
        dropPlayerForInvalid(playerToken, players, rooms, sockfd);
    }
}

// PING
// Klient → server:  ID;PING
// Server → klient:  ID;PONG
// Příklad: 2;PING -> 2;PONG
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
// Příklad: 3;LIST_ROOMS -> 3;ROOMS_EMPTY (pokud žádné místnosti)
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
    std::cout << "[LIST_ROOMS] key=" << addrToKey(clientAddr) << " rooms=" << rooms.size() << std::endl;
}
}

// CREATE_ROOM
// Klient → server:  ID;CREATE_ROOM;<name>
// Server → klient:  ID;CREATE_ROOM_OK;room=<roomId>
//                  nebo ID;ERROR;INVALID_FORMAT;Missing room name
//                  nebo ID;ERROR;INVALID_FORMAT;Invalid chars in room name
//                  nebo ID;ERROR;INVALID_FORMAT;Room name too long
//                  nebo ID;ERROR;SERVER_FULL;Rooms limit reached
// Příklad: 4;CREATE_ROOM;Room1 -> 4;CREATE_ROOM_OK;room=1
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
) {
    if (msg.rawParams.size() < 1) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Missing room name\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalidMessage(playerToken, players, rooms, sockfd, "INVALID_FORMAT");
        return;
    }

    std::string name = msg.rawParams[0];

    if (hasInvalidDelims(name)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Invalid chars in room name\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalidMessage(playerToken, players, rooms, sockfd, "INVALID_FORMAT");
        return;
    }
    if (exceedsLimit(name, 64)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Room name too long\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalidMessage(playerToken, players, rooms, sockfd, "INVALID_FORMAT");
        return;
    }

    if (rooms.size() >= static_cast<std::size_t>(limits.maxRooms)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;SERVER_FULL;Vyčerpán limit místností\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    Room room;
    room.id     = nextRoomId++;
    room.name   = "Stůl " + std::to_string(mutableLimits.nextTableIndex++);
    room.status = RoomStatus::WAITING;
    room.turn   = Turn::NONE;
    room.board.clear();

    rooms[room.id] = room;

    std::string resp = std::to_string(msg.id) +
                       ";CREATE_ROOM_OK;room=" +
                       std::to_string(room.id) +
                       ";name=" + room.name + "\n";

    sendto(sockfd, resp.c_str(), resp.size(), 0,
           reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);

    std::cout << "[INFO] CREATE_ROOM room=" << room.id
              << " name=" << room.name
              << " cap=" << ROOM_CAPACITY << std::endl;
    ;
}

// JOIN_ROOM
// Klient → server:  ID;JOIN_ROOM;<roomId>
// Server → klient:  ID;JOIN_ROOM_OK;room=<roomId>;players=<count>/<ROOM_CAPACITY>
//                  nebo ID;ERROR;ROOM_NOT_FOUND|NOT_LOGGED_IN|ROOM_FULL|ROOM_NOT_AVAILABLE
// Když se room naplní:
//    všem:          ID;GAME_START;room=<roomId>;you=<WHITE|BLACK>
//    všem:          ID;GAME_STATE;room=<roomId>;turn=PLAYER1;board=<64 chars>
// Příklad: 5;JOIN_ROOM;1 -> 5;JOIN_ROOM_OK;room=1;players=1/2
void handleJoinRoom(
    const Message& msg,
    const std::string& playerToken,
    RoomsMap& rooms,
    PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen,
    int turnTimeoutMs
) {
    auto registerInvalid = [&](const std::string& code) {
        registerInvalidMessage(playerToken, players, rooms, sockfd, code);
    };

    if (msg.rawParams.size() < 1) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Missing roomId\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("INVALID_FORMAT");
        return;
    }

    int roomId = 0;
    if (!parseInt(msg.rawParams[0], roomId)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;roomId must be number\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("INVALID_FORMAT");
        return;
    }

    auto itRoom = rooms.find(roomId);
    if (itRoom == rooms.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;ROOM_NOT_FOUND\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("ROOM_NOT_FOUND");
        return;
    }

    auto itPlayer = players.find(playerToken);
    if (itPlayer == players.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_LOGGED_IN\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NOT_LOGGED_IN");
        return;
    }

    Room& room = itRoom->second;

    if (room.status != RoomStatus::WAITING) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;ROOM_NOT_AVAILABLE\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    if (room.playerKeys.size() >= ROOM_CAPACITY) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;ROOM_FULL\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    // přidáme klienta, pokud tam ještě není
    if (std::find(room.playerKeys.begin(), room.playerKeys.end(), playerToken) ==
        room.playerKeys.end()) {
        room.playerKeys.push_back(playerToken);
    }

    // odpověď JOIN_ROOM_OK jen volajícímu klientovi
    std::string resp = std::to_string(msg.id) +
                       ";JOIN_ROOM_OK;room=" + std::to_string(room.id) +
                       ";players=" + std::to_string(room.playerKeys.size()) +
                       "/" + std::to_string(ROOM_CAPACITY) + "\n";

    sendto(sockfd, resp.c_str(), resp.size(), 0,
           reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);

    std::cout << "[INFO] JOIN room=" << room.id
              << " key=" << playerToken
              << " size=" << room.playerKeys.size() << "/" << ROOM_CAPACITY
              << " status=" << (room.status == RoomStatus::WAITING ? "WAITING" : "IN_GAME")
              << std::endl;

    // pokud je room plná -> spustit hru
    if (room.playerKeys.size() >= ROOM_CAPACITY) {
        room.status = RoomStatus::IN_GAME;
        room.turn   = Turn::PLAYER1;
        room.board  = createInitialBoard();
        room.captureLock.reset();
        room.remainingTurnMs = turnTimeoutMs;
        room.lastTurnAt = std::chrono::steady_clock::now();

        // každému hráči pošleme GAME_START (role WHITE/BLACK)
        for (std::size_t i = 0; i < ROOM_CAPACITY; i++) {
            const std::string& pKey = room.playerKeys[i];
            auto pit = players.find(pKey);
            if (pit == players.end()) continue;

            const Player& p = pit->second;
            sockaddr_in pAddr = p.addr;
            socklen_t pLen = sizeof(pAddr);

            std::string role = (i == 0) ? "WHITE" : "BLACK";
            std::string opponentNick;
            if (ROOM_CAPACITY == 2 && room.playerKeys.size() >= 2) {
                std::size_t oppIndex = (i == 0) ? 1 : 0;
                const std::string& oppKey = room.playerKeys[oppIndex];
                auto oppIt = players.find(oppKey);
                if (oppIt != players.end()) {
                    opponentNick = oppIt->second.nick;
                }
            }

            std::string startMsg = std::to_string(msg.id) +
                                   ";GAME_START;room=" + std::to_string(room.id) +
                                   ";you=" + role;
            if (!opponentNick.empty()) {
                startMsg += ";opponent=" + opponentNick;
            }
            startMsg += "\n";

            sendto(sockfd, startMsg.c_str(), startMsg.size(), 0,
                   reinterpret_cast<const sockaddr*>(&pAddr), pLen);
        }

        // immediately send GAME_STATE with board to all
        broadcastGameState(msg.id, room, players, sockfd, turnTimeoutMs);

    std::cout << "[INFO] GAME_START room=" << room.id
              << " white=" << room.playerKeys[0]
              << " black=" << room.playerKeys[1] << std::endl;
    std::cout << "[INFO] GAME_STATE turn=" << turnToString(room.turn) << " board=" << room.board << std::endl;
}
}

// MOVE
// Klient → server:  ID;MOVE;<roomId>;<fromRow>;<fromCol>;<toRow>;<toCol>
// Server → klient:  ID;ERROR;<CODE>
//   kódy: INVALID_FORMAT|ROOM_NOT_FOUND|ROOM_NOT_IN_GAME|NOT_LOGGED_IN|NOT_IN_ROOM|
//         NOT_YOUR_TURN|OUT_OF_BOARD|INVALID_SQUARE|NO_PIECE|NOT_YOUR_PIECE|
//         DEST_NOT_EMPTY|INVALID_MOVE|INVALID_DIRECTION|MUST_CAPTURE|
//         MUST_CONTINUE_CAPTURE|NO_OPPONENT_TO_CAPTURE
// Při úspěchu: všem v room: ID;GAME_STATE;room=<roomId>;turn=<PLAYER1|PLAYER2>;board=<64 chars>
// Příklad: 6;MOVE;1;5;0;4;1 -> 6;GAME_STATE;room=1;turn=PLAYER2;board=...
void handleMove(
    const Message& msg,
    const std::string& playerToken,
    RoomsMap& rooms,
    PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen,
    int turnTimeoutMs
) {
    auto registerInvalid = [&](const std::string& code) {
        registerInvalidMessage(playerToken, players, rooms, sockfd, code);
    };

    if (msg.rawParams.size() < 5) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Missing roomId/fromRow/fromCol/toRow/toCol\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("INVALID_FORMAT");
        return;
    }

    int roomId, fromRow, fromCol, toRow, toCol;
    if (!parseInt(msg.rawParams[0], roomId) ||
        !parseInt(msg.rawParams[1], fromRow) ||
        !parseInt(msg.rawParams[2], fromCol) ||
        !parseInt(msg.rawParams[3], toRow) ||
        !parseInt(msg.rawParams[4], toCol)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Coordinates must be numbers\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("INVALID_FORMAT");
        return;
    }

    auto itRoom = rooms.find(roomId);
    if (itRoom == rooms.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;ROOM_NOT_FOUND\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("ROOM_NOT_FOUND");
        return;
    }

    Room& room = itRoom->second;

    if (room.status != RoomStatus::IN_GAME) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;ROOM_NOT_IN_GAME\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("ROOM_NOT_IN_GAME");
        return;
    }

    // najdeme index hráče v room
    auto itKey = std::find(room.playerKeys.begin(), room.playerKeys.end(), playerToken);
    if (itKey == room.playerKeys.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_IN_ROOM\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NOT_IN_ROOM");
        return;
    }

    std::size_t playerIndex = static_cast<std::size_t>(itKey - room.playerKeys.begin());
    auto itPlayerObj = players.find(playerToken);
    if (itPlayerObj == players.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_LOGGED_IN\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NOT_LOGGED_IN");
        return;
    }
    // deduplikace MOVE (ignoruj stejné nebo starší msg.id)
    if (msg.id <= itPlayerObj->second.lastMoveMsgId) {
        return;
    }
    itPlayerObj->second.lastMoveMsgId = msg.id;

    // kontrola, jestli je na tahu
    if ((room.turn == Turn::PLAYER1 && playerIndex != 0) ||
        (room.turn == Turn::PLAYER2 && playerIndex != 1)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_YOUR_TURN\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NOT_YOUR_TURN");
        return;
    }

    if (roomHasPausedPlayer(room, players)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;GAME_PAUSED\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("GAME_PAUSED");
        return;
    }

    if (room.captureLock.has_value()) {
        auto [lockRow, lockCol] = *room.captureLock;
        if (fromRow != lockRow || fromCol != lockCol) {
            std::string resp = std::to_string(msg.id) +
                               ";ERROR;MUST_CONTINUE_CAPTURE\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
            registerInvalid("MUST_CONTINUE_CAPTURE");
            return;
        }
    }

    // kontrola rozsahu
    auto inRange = [](int v) { return v >= 0 && v < BOARD_SIZE; };
    if (!inRange(fromRow) || !inRange(fromCol) ||
        !inRange(toRow)   || !inRange(toCol)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;OUT_OF_BOARD\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("OUT_OF_BOARD");
        return;
    }

    if (!isDarkSquare(fromRow, fromCol) || !isDarkSquare(toRow, toCol)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_SQUARE\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("INVALID_SQUARE");
        return;
    }

    char pieceFrom = getPiece(room, fromRow, fromCol);
    char pieceTo   = getPiece(room, toRow, toCol);

    if (pieceFrom == '.') {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NO_PIECE\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NO_PIECE");
        return;
    }

    // určíme barvu hráče: 0 = WHITE (w), 1 = BLACK (b)
    bool isWhitePlayer = (playerIndex == 0);
    if (pieceColor(pieceFrom) == PieceColor::NONE ||
        (isWhitePlayer && pieceColor(pieceFrom) != PieceColor::WHITE) ||
        (!isWhitePlayer && pieceColor(pieceFrom) != PieceColor::BLACK)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_YOUR_PIECE\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NOT_YOUR_PIECE");
        return;
    }

    if (pieceTo != '.') {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;DEST_NOT_EMPTY\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("DEST_NOT_EMPTY");
        return;
    }

    int dRow = toRow - fromRow;
    int dCol = toCol - fromCol;

    if (std::abs(dRow) != std::abs(dCol) || dRow == 0) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_MOVE\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("INVALID_MOVE");
        return;
    }

    PieceColor currentColor = isWhitePlayer ? PieceColor::WHITE : PieceColor::BLACK;
    bool captureAvailable   = playerHasAnyCapture(room, currentColor);

    bool isCapture = false;
    int captureRow = -1, captureCol = -1;
    bool pathInvalid = false;

    if (isKing(pieceFrom)) {
        int stepRow = (dRow > 0) ? 1 : -1;
        int stepCol = (dCol > 0) ? 1 : -1;
        int r = fromRow + stepRow;
        int c = fromCol + stepCol;
        int enemies = 0;

        while (r != toRow || c != toCol) {
            char cur = getPiece(room, r, c);
            if (cur != '.') {
                if (pieceColor(cur) == currentColor) {
                    pathInvalid = true;
                    break;
                }
                enemies++;
                captureRow = r;
                captureCol = c;
                if (enemies > 1) {
                    pathInvalid = true;
                    break;
                }
            }
            r += stepRow;
            c += stepCol;
        }

        if (pathInvalid) {
            std::string resp = std::to_string(msg.id) +
                               ";ERROR;INVALID_MOVE\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
            registerInvalid("INVALID_MOVE");
            return;
        }

        isCapture = (enemies == 1);
        if (enemies == 0 && captureAvailable) {
            std::string resp = std::to_string(msg.id) +
                               ";ERROR;MUST_CAPTURE\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
            registerInvalid("MUST_CAPTURE");
            return;
        }
    } else {
        bool isSimple = (std::abs(dRow) == 1 && std::abs(dCol) == 1);
        bool manCapture = (std::abs(dRow) == 2 && std::abs(dCol) == 2);

        auto dirOkForMan = [&](int stepRow) {
            if (isWhitePlayer) return stepRow == -1 || stepRow == -2;
            return stepRow == 1 || stepRow == 2;
        };

        if (!isSimple && !manCapture) {
            std::string resp = std::to_string(msg.id) +
                               ";ERROR;INVALID_MOVE\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
            registerInvalid("INVALID_MOVE");
            return;
        }

        if (!dirOkForMan(dRow)) {
            std::string resp = std::to_string(msg.id) +
                               ";ERROR;INVALID_DIRECTION\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
            registerInvalid("INVALID_DIRECTION");
            return;
        }

        if (isSimple && captureAvailable) {
            std::string resp = std::to_string(msg.id) +
                               ";ERROR;MUST_CAPTURE\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
            registerInvalid("MUST_CAPTURE");
            return;
        }

        if (manCapture) {
            captureRow = fromRow + dRow / 2;
            captureCol = fromCol + dCol / 2;
            char middlePiece = getPiece(room, captureRow, captureCol);
            if (pieceColor(middlePiece) == PieceColor::NONE ||
                pieceColor(middlePiece) == currentColor) {
                std::string resp = std::to_string(msg.id) +
                                   ";ERROR;NO_OPPONENT_TO_CAPTURE\n";
                sendto(sockfd, resp.c_str(), resp.size(), 0,
                       reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
                registerInvalid("NO_OPPONENT_TO_CAPTURE");
                return;
            }
            isCapture = true;
        }
    }

    if (isCapture) {
        setPiece(room, captureRow, captureCol, '.');
    }

    // provedeme tah
    setPiece(room, toRow, toCol, pieceFrom);
    setPiece(room, fromRow, fromCol, '.');

    // povýšení na dámu
    char placed = getPiece(room, toRow, toCol);
    if (!isKing(placed)) {
        if ((isWhitePlayer && toRow == 0) || (!isWhitePlayer && toRow == BOARD_SIZE - 1)) {
            placed = isWhitePlayer ? 'W' : 'B';
            setPiece(room, toRow, toCol, placed);
        }
    }

    bool captureContinues = false;
    if (isCapture) {
        PieceColor myColor = isWhitePlayer ? PieceColor::WHITE : PieceColor::BLACK;
        std::vector<std::pair<int, int>> furtherCaptures;
        if (isKing(placed)) {
            furtherCaptures = kingCaptureMoves(room, toRow, toCol, myColor);
        } else {
            furtherCaptures = manCaptureMoves(room, toRow, toCol, isWhitePlayer, myColor);
        }
        captureContinues = !furtherCaptures.empty();
    }

    std::cout << "[INFO] MOVE room=" << room.id
              << " from=" << fromRow << "," << fromCol
              << " to=" << toRow << "," << toCol
              << " player=" << (isWhitePlayer ? 1 : 2)
              << " capture=" << (isCapture ? 1 : 0)
              << " king=" << (isKing(placed) ? 1 : 0)
              << std::endl;

    if (captureContinues) {
        room.captureLock = std::make_pair(toRow, toCol);
    } else {
        room.captureLock.reset();
        room.turn = (room.turn == Turn::PLAYER1) ? Turn::PLAYER2 : Turn::PLAYER1;
    }
    room.remainingTurnMs = turnTimeoutMs;
    room.lastTurnAt = std::chrono::steady_clock::now();

    // vyhodnocení konce hry
    PieceColor opponentColor = isWhitePlayer ? PieceColor::BLACK : PieceColor::WHITE;
    bool opponentHasPieces = hasAnyPiece(room, opponentColor);
    bool opponentHasMoves  = playerHasAnyMove(room, opponentColor);

    broadcastGameState(msg.id, room, players, sockfd, turnTimeoutMs);

    if (!opponentHasPieces) {
        std::string reason = isWhitePlayer ? "WHITE_WIN_NO_PIECES" : "BLACK_WIN_NO_PIECES";
        sendGameEnd(msg.id, room, players, sockfd, reason);
        resetRoom(room);
    } else if (!opponentHasMoves) {
        std::string reason = isWhitePlayer ? "WHITE_WIN_NO_MOVES" : "BLACK_WIN_NO_MOVES";
        sendGameEnd(msg.id, room, players, sockfd, reason);
        resetRoom(room);
    }
}

// LEGAL_MOVES
// Klient → server:  ID;LEGAL_MOVES;<roomId>;<row>;<col>
// Server → klient:  ID;LEGAL_MOVES;room=<roomId>;from=<row,col>;to=<r1,c1>|<r2,c2>;mustCapture=<0|1>
// nebo:             ID;ERROR;INVALID_FORMAT|ROOM_NOT_FOUND|ROOM_NOT_IN_GAME|NOT_LOGGED_IN|NOT_IN_ROOM|NOT_YOUR_PIECE|NO_PIECE|MUST_CONTINUE_CAPTURE
void handleLegalMoves(
    const Message& msg,
    const std::string& playerToken,
    RoomsMap& rooms,
    PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
) {
    auto registerInvalid = [&](const std::string& code) {
        registerInvalidMessage(playerToken, players, rooms, sockfd, code);
    };

    if (msg.rawParams.size() < 3) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;Missing roomId/row/col\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("INVALID_FORMAT");
        return;
    }

    int roomId, row, col;
    if (!parseInt(msg.rawParams[0], roomId) ||
        !parseInt(msg.rawParams[1], row) ||
        !parseInt(msg.rawParams[2], col)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_FORMAT;roomId/row/col must be numbers\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("INVALID_FORMAT");
        return;
    }

    auto itRoom = rooms.find(roomId);
    if (itRoom == rooms.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;ROOM_NOT_FOUND\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("ROOM_NOT_FOUND");
        return;
    }
    Room& room = itRoom->second;

    if (room.status != RoomStatus::IN_GAME) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;ROOM_NOT_IN_GAME\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("ROOM_NOT_IN_GAME");
        return;
    }

    auto itPlayer = players.find(playerToken);
    if (itPlayer == players.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_LOGGED_IN\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NOT_LOGGED_IN");
        return;
    }

    auto itKey = std::find(room.playerKeys.begin(), room.playerKeys.end(), playerToken);
    if (itKey == room.playerKeys.end()) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_IN_ROOM\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NOT_IN_ROOM");
        return;
    }

    std::size_t playerIndex = static_cast<std::size_t>(itKey - room.playerKeys.begin());
    bool isWhitePlayer = playerIndex == 0;

    if (roomHasPausedPlayer(room, players)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;GAME_PAUSED\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("GAME_PAUSED");
        return;
    }

    auto inRange = [](int v) { return v >= 0 && v < BOARD_SIZE; };
    if (!inRange(row) || !inRange(col) || !isDarkSquare(row, col)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;INVALID_SQUARE\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("INVALID_SQUARE");
        return;
    }

    if (room.captureLock.has_value()) {
        auto [lockRow, lockCol] = *room.captureLock;
        if (row != lockRow || col != lockCol) {
            std::string resp = std::to_string(msg.id) +
                               ";ERROR;MUST_CONTINUE_CAPTURE\n";
            sendto(sockfd, resp.c_str(), resp.size(), 0,
                   reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
            registerInvalid("MUST_CONTINUE_CAPTURE");
            return;
        }
    }

    char pieceFrom = getPiece(room, row, col);
    if (pieceFrom == '.') {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NO_PIECE\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NO_PIECE");
        return;
    }

    if ((isWhitePlayer && pieceColor(pieceFrom) != PieceColor::WHITE) ||
        (!isWhitePlayer && pieceColor(pieceFrom) != PieceColor::BLACK)) {
        std::string resp = std::to_string(msg.id) +
                           ";ERROR;NOT_YOUR_PIECE\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NOT_YOUR_PIECE");
        return;
    }

    PieceColor myColor = isWhitePlayer ? PieceColor::WHITE : PieceColor::BLACK;
    bool globalCaptureAvailable = playerHasAnyCapture(room, myColor) || room.captureLock.has_value();
    std::vector<std::pair<int, int>> captureMoves;
    std::vector<std::pair<int, int>> simpleMoves;

    if (isKing(pieceFrom)) {
        captureMoves = kingCaptureMoves(room, row, col, myColor);
        if (!globalCaptureAvailable) {
            simpleMoves = kingSimpleMoves(room, row, col);
        }
    } else {
        captureMoves = manCaptureMoves(room, row, col, isWhitePlayer, myColor);
        if (!globalCaptureAvailable) {
            simpleMoves = manSimpleMoves(room, row, col, isWhitePlayer);
        }
    }

    std::vector<std::pair<int, int>> dests;
    bool mustCaptureFlag = false;

    if (!captureMoves.empty()) {
        dests = captureMoves;
        mustCaptureFlag = true;
    } else if (globalCaptureAvailable) {
        dests.clear();
        mustCaptureFlag = true;
    } else {
        dests = simpleMoves;
        mustCaptureFlag = false;
    }

    std::stringstream ss;
    ss << msg.id << ";LEGAL_MOVES;"
       << "room=" << room.id
       << ";from=" << row << "," << col
       << ";to=";
    for (std::size_t i = 0; i < dests.size(); ++i) {
        ss << dests[i].first << "," << dests[i].second;
        if (i + 1 < dests.size()) ss << "|";
    }
    ss << ";mustCapture=" << (mustCaptureFlag ? 1 : 0) << "\n";

    auto resp = ss.str();
    sendto(sockfd, resp.c_str(), resp.size(), 0,
           reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
}

// LEAVE_ROOM
// Klient → server:  ID;LEAVE_ROOM;<roomId>
// Server → klient:  ID;LEAVE_ROOM_OK;room=<roomId>
//                  nebo ID;ERROR;ROOM_NOT_FOUND|NOT_LOGGED_IN|NOT_IN_ROOM
// Pokud zůstal soupeř v IN_GAME:
//    soupeř:        ID;GAME_END;room=<roomId>;reason=OPPONENT_LEFT
// Příklad: 7;LEAVE_ROOM;1 -> 7;LEAVE_ROOM_OK;room=1
void handleLeaveRoom(
    const Message& msg,
    const std::string& playerToken,
    RoomsMap& rooms,
    PlayersMap& players,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen,
    int reconnectWindowMs
) {
    auto registerInvalid = [&](const std::string& code) {
        registerInvalidMessage(playerToken, players, rooms, sockfd, code);
    };

    if (msg.rawParams.size() < 1) {
        std::string resp = std::to_string(msg.id) +
                            ";ERROR;INVALID_FORMAT;Missing roomId\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
                reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("INVALID_FORMAT");
        return;
    }

    int roomId = 0;
    if (!parseInt(msg.rawParams[0], roomId)) {
        std::string resp = std::to_string(msg.id) +
                            ";ERROR;INVALID_FORMAT;roomId must be number\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
                reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("INVALID_FORMAT");
        return;
    }

    auto itRoom = rooms.find(roomId);
    if (itRoom == rooms.end()) {
        std::string resp = std::to_string(msg.id) +
            ";ERROR;ROOM_NOT_FOUND\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("ROOM_NOT_FOUND");
        return;
    }

    auto itPlayer = players.find(playerToken);
    if (itPlayer == players.end()) {
        std::string resp = std::to_string(msg.id) +
            ";ERROR;NOT_LOGGED_IN\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
                reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NOT_LOGGED_IN");
        return;
    }

    Room& room = itRoom->second;

    // najít hráče
    auto itKey = std::find(room.playerKeys.begin(), room.playerKeys.end(), playerToken);
    if (itKey == room.playerKeys.end()) {
        std::string resp = std::to_string(msg.id) +
                          ";ERROR;NOT_IN_ROOM\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        registerInvalid("NOT_IN_ROOM");
        return;
    }

    // zapamatuj, zda odchází hráč na pozici 0 (WHITE) nebo 1 (BLACK)
    bool leavingWasWhite = (std::distance(room.playerKeys.begin(), itKey) == 0);
    room.playerKeys.erase(itKey);

    // potvrzení
    std::string resp = std::to_string(msg.id) +
                        ";LEAVE_ROOM_OK;room=" + std::to_string(roomId) + "\n";
    sendto(sockfd, resp.c_str(), resp.size(), 0,
            reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);

    std::cout << "[INFO] LEAVE room=" << room.id
              << " key=" << playerToken << std::endl;

    // clean-up prázdné room

    if (room.playerKeys.empty()) {
        resetRoom(room);
        return;
    }

    // pokud zůstal hráč (druhý se najednou odpojil)
    if (room.status == RoomStatus::IN_GAME) {
        const std::string& remainingKey = room.playerKeys[0];
        auto pit = players.find(remainingKey);
        if (pit != players.end()) {
            const Player& player = pit->second;
            sockaddr_in playerAddr = player.addr;
            socklen_t playerLen = sizeof(playerAddr);

            std::string winner = leavingWasWhite ? "BLACK" : "WHITE";
            sendGameEnd(msg.id, room, players, sockfd, "OPPONENT_LEFT", winner);

        }

        resetRoom(room);
    }
}
void checkTimeouts(
    PlayersMap& players,
    RoomsMap& rooms,
    int heartbeatTimeoutMs,
    int pauseThresholdMs,
    int turnTimeoutMs,
    int sockfd,
    int reconnectWindowMs,
    EndpointMap& endpointToToken
) {
    auto now = std::chrono::steady_clock::now();

    if (pauseThresholdMs > 0) {
        for (auto& [roomId, room] : rooms) {
            if (room.status != RoomStatus::IN_GAME) continue;
            if (room.lastTurnAt == std::chrono::steady_clock::time_point{}) continue;
            if (room.playerKeys.empty()) continue;

            bool anyPlayer = false;
            bool allStale = true;
            auto freezeAt = std::chrono::steady_clock::time_point{};
            for (const auto& key : room.playerKeys) {
                auto pit = players.find(key);
                if (pit == players.end()) continue;
                anyPlayer = true;
                if (freezeAt == std::chrono::steady_clock::time_point{} || pit->second.lastSeen > freezeAt) {
                    freezeAt = pit->second.lastSeen;
                }
                auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - pit->second.lastSeen).count();
                if (elapsed <= pauseThresholdMs) {
                    allStale = false;
                    break;
                }
            }

            if (anyPlayer && allStale) {
                auto effectiveFreezeAt = freezeAt == std::chrono::steady_clock::time_point{} ? now : freezeAt;
                auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(effectiveFreezeAt - room.lastTurnAt).count();
                if (elapsed < 0) {
                    elapsed = 0;
                }
                room.remainingTurnMs = std::max(0, turnTimeoutMs - static_cast<int>(elapsed));
                room.lastTurnAt = std::chrono::steady_clock::time_point{}; // freeze timer during server outage
            }
        }
    }

    for (auto& [key, player] : players) {
        auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - player.lastSeen).count();
        if (elapsed > heartbeatTimeoutMs && !player.paused) {
            std::cout << "Player timeout: " << player.nick
                      << " key=" << key
                      << " addr=" << addrToKey(player.addr) << std::endl;
            player.connected = false;
            // mark pause window
            player.paused = true;
            player.resumeDeadline = now + std::chrono::milliseconds(reconnectWindowMs);

            for (auto& [roomId, room] : rooms) {
                auto it = std::find(room.playerKeys.begin(), room.playerKeys.end(), key);
                if (it == room.playerKeys.end()) continue;

                if (room.status == RoomStatus::IN_GAME) {
                    pauseRoom(room, players, sockfd, reconnectWindowMs, turnTimeoutMs, key);
                    std::cout << "[WARN] TIMEOUT_HEARTBEAT room=" << room.id
                              << " key=" << key << " paused" << std::endl;
                } else {
                    room.playerKeys.erase(it);
                    if (room.playerKeys.empty()) {
                        resetRoom(room);
                    }
                }
            }
        }
    }

    for (auto& [roomId, room] : rooms) {
        if (room.status != RoomStatus::IN_GAME) continue;
        if (room.lastTurnAt == std::chrono::steady_clock::time_point{}) continue;

        auto elapsed = std::chrono::duration_cast<std::chrono::milliseconds>(now - room.lastTurnAt).count();
        if (elapsed > turnTimeoutMs) {
            std::cout << "[WARN] TURN_TIMEOUT room=" << room.id << std::endl;
            // vyhodnotí prohru hráče na tahu
            std::string offenderKey;
            std::string winner = "NONE";
            if (room.turn == Turn::PLAYER1 && !room.playerKeys.empty()) {
                offenderKey = room.playerKeys[0];
                if (room.playerKeys.size() > 1) winner = "BLACK";
            } else if (room.turn == Turn::PLAYER2 && room.playerKeys.size() > 1) {
                offenderKey = room.playerKeys[1];
                winner = "WHITE";
            }
            sendGameEnd(0, room, players, sockfd, "TURN_TIMEOUT", winner);
            resetRoom(room);
        }
    }

    // Expire paused players
    std::vector<std::string> toErase;
    for (auto& [key, player] : players) {
        if (player.paused && player.resumeDeadline != std::chrono::steady_clock::time_point{}) {
            if (now > player.resumeDeadline) {
                std::cout << "[WARN] RECONNECT_TIMEOUT key=" << key << std::endl;
                // najde místnost a ukončí hru pro druhého hráče
                for (auto& [roomId, room] : rooms) {
                    auto it = std::find(room.playerKeys.begin(), room.playerKeys.end(), key);
                    if (it != room.playerKeys.end()) {
                        if (!room.playerKeys.empty() && room.status == RoomStatus::IN_GAME) {
                            std::string winnerOverride = "NONE";
                            if (room.playerKeys.size() >= 2) {
                                bool timedOutWasWhite = (it == room.playerKeys.begin());
                                const std::string& opponentKey = timedOutWasWhite ? room.playerKeys[1] : room.playerKeys[0];
                                auto pitOpp = players.find(opponentKey);
                                if (pitOpp != players.end()) {
                                    const Player& opponent = pitOpp->second;
                                    if (!opponent.paused ||
                                        opponent.resumeDeadline == std::chrono::steady_clock::time_point{} ||
                                        opponent.resumeDeadline > now) {
                                        winnerOverride = timedOutWasWhite ? "BLACK" : "WHITE";
                                    }
                                }
                            }
                            sendGameEnd(0, room, players, sockfd, "OPPONENT_TIMEOUT", winnerOverride);
                        }
                        resetRoom(room);
                    }
                }
                player.paused = false;
                player.resumeDeadline = std::chrono::steady_clock::time_point{};
                for (auto it = endpointToToken.begin(); it != endpointToToken.end();) {
                    if (it->second == player.token) {
                        it = endpointToToken.erase(it);
                    } else {
                        ++it;
                    }
                }
                toErase.push_back(key);
            }
        }
    }
    for (const auto& k : toErase) {
        players.erase(k);
    }

    // pokud v IN_GAME room nejsou připojení hráči a všichni mají propadlé deadline -> reset
    for (auto& [roomId, room] : rooms) {
        if (room.status != RoomStatus::IN_GAME) continue;
        bool anyConnected = false;
        for (const auto& key : room.playerKeys) {
            auto pit = players.find(key);
            if (pit != players.end() && pit->second.connected) {
                anyConnected = true;
                break;
            }
        }
        if (!anyConnected) {
            bool allExpired = true;
            for (const auto& key : room.playerKeys) {
                auto pit = players.find(key);
                if (pit != players.end()) {
                    if (pit->second.resumeDeadline == std::chrono::steady_clock::time_point{} ||
                        pit->second.resumeDeadline > now) {
                        allExpired = false;
                        break;
                    }
                }
            }
            if (allExpired) {
                resetRoom(room);
            }
        }
    }
}

void handleBye(
    const Message& msg,
    const std::string& playerToken,
    PlayersMap& players,
    RoomsMap& rooms,
    EndpointMap& endpointToToken,
    int sockfd,
    const sockaddr_in& clientAddr,
    socklen_t clientLen
) {
    auto pit = players.find(playerToken);
    if (pit == players.end()) {
        std::string resp = std::to_string(msg.id) + ";BYE_OK\n";
        sendto(sockfd, resp.c_str(), resp.size(), 0,
               reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
        return;
    }

    Player player = pit->second;

    // odstranění z místností a notifikace soupeře
    for (auto& [roomId, room] : rooms) {
        auto it = std::find(room.playerKeys.begin(), room.playerKeys.end(), playerToken);
        if (it != room.playerKeys.end()) {
            if (!room.playerKeys.empty() && room.status == RoomStatus::IN_GAME) {
                sendGameEnd(msg.id, room, players, sockfd, "OPPONENT_LEFT");
            }
            resetRoom(room);
        }
    }

    for (auto it = endpointToToken.begin(); it != endpointToToken.end();) {
        if (it->second == player.token) {
            it = endpointToToken.erase(it);
        } else {
            ++it;
        }
    }
    players.erase(playerToken);

    std::string resp = std::to_string(msg.id) + ";BYE_OK\n";
    sendto(sockfd, resp.c_str(), resp.size(), 0,
           reinterpret_cast<const sockaddr*>(&clientAddr), clientLen);
    std::cout << "[INFO] BYE key=" << playerToken << " - removed player" << std::endl;
}

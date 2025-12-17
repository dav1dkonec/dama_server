#include "protocol.hpp"

#include <sstream>
#include <iostream>
#include <arpa/inet.h>   // inet_ntop

std::vector<std::string> split(const std::string& s, char delim) {
    std::vector<std::string> parts;
    std::stringstream ss(s);
    std::string item;
    while (std::getline(ss, item, delim)) {
        parts.push_back(item);
    }
    return parts;
}

void rtrim(std::string& s) {
    while (!s.empty() &&
           (s.back() == '\n' || s.back() == '\r' ||
            s.back() == ' '  || s.back() == '\t')) {
        s.pop_back();
    }
}

bool parseMessage(const std::string& line, Message& msg) {
    auto parts = split(line, ';');
    if (parts.size() < 2) {
        return false;
    }

    try {
        msg.id = std::stoi(parts[0]);
    } catch (...) {
        return false;
    }

    msg.type = parts[1];
    msg.rawParams.clear();
    msg.kvParams.clear();

    for (size_t i = 2; i < parts.size(); ++i) {
        const std::string& p = parts[i];
        msg.rawParams.push_back(p);

        auto eqPos = p.find('=');
        if (eqPos != std::string::npos) {
            std::string key = p.substr(0, eqPos);
            std::string value = p.substr(eqPos + 1);
            msg.kvParams[key] = value;
        }
    }

    return true;
}

std::string addrToKey(const sockaddr_in& addr) {
    char ip[INET_ADDRSTRLEN];
    inet_ntop(AF_INET, &addr.sin_addr, ip, sizeof(ip));
    uint16_t port = ntohs(addr.sin_port);
    std::stringstream ss;
    ss << ip << ":" << port;
    return ss.str();
}
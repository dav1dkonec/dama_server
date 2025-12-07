#pragma once

#include <string>
#include <vector>
#include <map>
#include <netinet/in.h>   // sockaddr_in

// Struktura jedné zprávy
struct Message {
    int id = 0;
    std::string type;
    std::vector<std::string> rawParams;                // parametry tak, jak přijdou
    std::map<std::string, std::string> kvParams;       // key=value páry
};

// Pomocné funkce
std::vector<std::string> split(const std::string& s, char delim);

// oříznutí whitespace na konci
void rtrim(std::string& s);

// parsování "ID;TYPE;param;key=val;..."
bool parseMessage(const std::string& line, Message& msg);

// IP:port -> "127.0.0.1:5000"
std::string addrToKey(const sockaddr_in& addr);
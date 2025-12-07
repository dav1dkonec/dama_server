#!/usr/bin/env python3
"""
Rychlý manuální UDP skript pro ověření serveru.
Nastav HOST/PORT podle spuštěného serveru.
Posílá sekvenci: LOGIN dvou hráčů, CREATE_ROOM, JOIN_ROOM obou, LEGAL_MOVES, MOVE.
"""

import socket
import time

HOST = "127.0.0.1"
PORT = 5000


def recv_all(sock, first_timeout=1.0, drain_timeout=0.1):
    """
    Drain all queued datagrams for the socket.
    The server pushes async events (GAME_START/GAME_STATE) so we need to read
    everything pending, not just a single reply.
    """
    original_timeout = sock.gettimeout()
    messages = []
    try:
        sock.settimeout(first_timeout)
        while True:
            try:
                data, _ = sock.recvfrom(2048)
                messages.append(data.decode().strip())
                sock.settimeout(drain_timeout)  # switch to short timeout to drain the rest
            except socket.timeout:
                break
    finally:
        sock.settimeout(original_timeout)
    return messages


def send(sock, msg):
    print(f"> {msg.strip()}")
    sock.sendto((msg + "\n").encode(), (HOST, PORT))
    responses = recv_all(sock)
    if not responses:
        print("< (timeout)")
    else:
        for resp in responses:
            print(f"< {resp}")


def main():
    sock1 = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock2 = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
    sock1.settimeout(1.0)
    sock2.settimeout(1.0)

    # Player 1 login
    send(sock1, "1;LOGIN;alice")
    # Player 2 login
    send(sock2, "2;LOGIN;bob")

    # Create room by player 1
    send(sock1, "3;CREATE_ROOM;TestRoom")
    # Player 1 join
    send(sock1, "4;JOIN_ROOM;1")
    # Player 2 join (triggers GAME_START and GAME_STATE)
    send(sock2, "5;JOIN_ROOM;1")

    time.sleep(0.5)

    # Ask legal moves for white piece at 5,0 (should have a move to 4,1)
    send(sock1, "6;LEGAL_MOVES;1;5;0")

    # Make a simple move (white) 5,0 -> 4,1
    send(sock1, "7;MOVE;1;5;0;4;1")

    # Ping to keep alive
    send(sock1, "8;PING")
    send(sock2, "9;PING")

    sock1.close()
    sock2.close()


if __name__ == "__main__":
    main()

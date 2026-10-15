#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A fake APRS-IS server for end-to-end runs: logs anyone in unverified and sends a status packet a second from
fictional callsigns, so nothing reaches the real network.

    python3 tools/ci/fake-aprsis.py 14580
"""
import socket
import sys
import threading
import time


def serve(connection, number):
    stream = connection.makefile("rwb")
    try:
        stream.write(b"# fake-aprsis 1.0\r\n")
        stream.flush()
        login = stream.readline().decode("latin-1").strip()
        print(f"session {number}: {login}", flush=True)
        stream.write(f"# logresp N0TEST unverified, server FAKE{number}\r\n".encode())
        stream.flush()
        for i in range(100000):
            stream.write(f"N0SRC-{i % 9 + 1}>APRS:>fake packet {i}\r\n".encode())
            stream.flush()
            time.sleep(1)
    except OSError:
        pass


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 14580
    server = socket.socket()
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", port))
    server.listen()
    number = 0
    while True:
        connection, _ = server.accept()
        number += 1
        threading.Thread(target=serve, args=(connection, number), daemon=True).start()


if __name__ == "__main__":
    main()

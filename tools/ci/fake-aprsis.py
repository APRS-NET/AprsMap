#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""A fake APRS-IS server for end-to-end runs: logs anyone in unverified and sends a packet a second from fictional
callsigns, so nothing reaches the real network.

    python3 tools/ci/fake-aprsis.py 14580                     # status packets only
    python3 tools/ci/fake-aprsis.py 14580 --around 40.0,-105.2  # also 40 stations with positions near a point
    python3 tools/ci/fake-aprsis.py 14580 --around 40.0,-105.2 --count 2000  # a load test: 2,000 stations
"""
import math
import random
import socket
import sys
import threading
import time

# A mix like the real feed's (spike 3's census): weather, digipeater, car, house, gateway with overlay, and others.
SYMBOLS = ["/_", "/#", "/>", "/-", "D&", "/k", "/v", "/j", "/[", "/r", "S#", "/Y", "/O", "\\>"]


def aprs_position(lat, lon, symbol, comment):
    ns = "N" if lat >= 0 else "S"
    ew = "E" if lon >= 0 else "W"
    lat, lon = abs(lat), abs(lon)
    lat_text = f"{int(lat):02d}{(lat - int(lat)) * 60:05.2f}{ns}"
    lon_text = f"{int(lon):03d}{(lon - int(lon)) * 60:05.2f}{ew}"
    return f"!{lat_text}{symbol[0]}{lon_text}{symbol[1]}{comment}"


def stations(around, count):
    if around is None:
        return []
    lat0, lon0 = around
    rng = random.Random(42)
    spread = max(1.0, math.sqrt(count / 40))
    return [
        {
            "call": f"N{i % 10}SIM-{i // 10 + 1}",
            "lat": lat0 + rng.uniform(-0.4, 0.4) * spread,
            "lon": lon0 + rng.uniform(-0.5, 0.5) * spread,
            "symbol": SYMBOLS[i % len(SYMBOLS)],
            "moving": i % 3 == 0,
            "heading": rng.uniform(0, 2 * math.pi),
        }
        for i in range(count)
    ]


def serve(connection, number, around, count):
    stream = connection.makefile("rwb")
    fleet = stations(around, count)
    try:
        stream.write(b"# fake-aprsis 1.0\r\n")
        stream.flush()
        login = stream.readline().decode("latin-1").strip()
        print(f"session {number}: {login}", flush=True)
        stream.write(f"# logresp N0TEST unverified, server FAKE{number}\r\n".encode())
        stream.flush()
        for station in fleet:  # everyone once at the start, so the map fills at once
            line = f"{station['call']}>APRS,TCPIP*,qAC,FAKE:{aprs_position(station['lat'], station['lon'], station['symbol'], ' simulated')}"
            stream.write((line + "\r\n").encode())
        stream.flush()
        for i in range(100000):
            stream.write(f"N0SRC-{i % 9 + 1}>APRS:>fake packet {i}\r\n".encode())
            if i % 15 == 7:
                # A malformed position now and then, for the packet list's parse issues.
                stream.write(f"N0BAD-{i % 3 + 1}>APRS,TCPIP*:!9999.99N/99999.99W- malformed {i}\r\n".encode())
            # Each moving station reports every 10 s, staggered, as real trackers do (about 70 packets a second
            # for 2,000 stations, under a full APRS-IS feed's rate).
            for n, station in enumerate(fleet):
                if station["moving"] and (n + i) % 10 == 0:
                    station["lat"] += 0.02 * math.cos(station["heading"])
                    station["lon"] += 0.02 * math.sin(station["heading"])
                    line = f"{station['call']}>APRS,TCPIP*,qAC,FAKE:{aprs_position(station['lat'], station['lon'], station['symbol'], ' moving')}"
                    stream.write((line + "\r\n").encode())
            stream.flush()
            time.sleep(1)
    except OSError:
        pass


def main():
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 14580
    around = None
    count = int(sys.argv[sys.argv.index("--count") + 1]) if "--count" in sys.argv else 40
    if "--around" in sys.argv:
        lat, lon = sys.argv[sys.argv.index("--around") + 1].split(",")
        around = (float(lat), float(lon))
    server = socket.socket()
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.bind(("127.0.0.1", port))
    server.listen()
    number = 0
    while True:
        connection, _ = server.accept()
        number += 1
        threading.Thread(target=serve, args=(connection, number, around, count), daemon=True).start()


if __name__ == "__main__":
    main()

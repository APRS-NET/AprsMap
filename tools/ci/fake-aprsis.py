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


def weather(rng):
    """APRS101 ch. 12 readings after a weather station's position: wind direction/speed, gust, temperature, humidity
    and pressure, so the map's weather labels and barbs have something to show (F1.10)."""
    return (f"{rng.randrange(0, 360):03d}/{rng.randrange(0, 40):03d}g{rng.randrange(5, 60):03d}"
            f"t{rng.randrange(20, 90):03d}h{rng.randrange(20, 99):02d}b{rng.randrange(9900, 10300):05d}")


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


# The IGates and digipeaters the paths name, with positions near the stations so a route can be drawn (F1.21).
INFRASTRUCTURE = [("N0GATE-1", 0.1, 0.1, "/&"), ("N0GATE-2", -0.2, -0.1, "/&"), ("N0DIGI-1", 0.15, -0.2, "/#"),
                  ("N0DIGI-2", -0.1, 0.25, "/#")]

# How the stations' reports arrive: heard directly by an IGate, through a digipeater, or from the internet.
PATHS = ["WIDE1-1,qAR,N0GATE-1", "N0DIGI-1*,WIDE2-1,qAR,N0GATE-1", "TCPIP*,qAC,FAKE", "N0DIGI-2*,WIDE2-1,qAR,N0GATE-2"]


def latlon(lat, lon):
    ns = "N" if lat >= 0 else "S"
    ew = "E" if lon >= 0 else "W"
    lat, lon = abs(lat), abs(lon)
    return f"{int(lat):02d}{(lat - int(lat)) * 60:05.2f}{ns}", f"{int(lon):03d}{(lon - int(lon)) * 60:05.2f}{ew}"


# Area objects and a signpost (APRS101 ch. 11; F1.12): a filled ellipse, a box outline, a line with a 10-mile corridor,
# and a speed-limit sign.
AREAS = [("SEARCH", 0.05, -0.3, "l710/310"), ("CLOSED", -0.25, 0.05, "l430/415"), ("COURSE", 0.2, 0.15, "l615/320{10}")]


def beacon_infrastructure(stream, around):
    if around is None:
        return
    for name, dlat, dlon, ext in AREAS:
        lat, lon = latlon(around[0] + dlat, around[1] + dlon)
        stream.write(f"N0NET>APRS,TCPIP*,qAC,FAKE:;{name:<9}*092345z{lat}\\{lon}{ext}\r\n".encode())
    lat, lon = latlon(around[0] - 0.05, around[1] + 0.2)
    stream.write(f"N0NET>APRS,TCPIP*,qAC,FAKE:)I25 3N!{lat}\\{lon}m{{55}}\r\n".encode())
    lat, lon = latlon(around[0] + 0.12, around[1] - 0.05)
    stream.write(f"N0TLM>APRS,TCPIP*,qAC,FAKE:!{lat}/{lon}r telemetry\r\n".encode())
    # Its definitions (APRS101 ch. 13), as messages to itself: names, units, scaling and the bits' sense (F1.11).
    for text in ["PARM.Battery,Temp,Light,Spare,Spare,Door,Fan,Heat", "UNIT.V,deg.C,%,,,open,on,on",
                 "EQNS.0,0.06,0,0,0.5,-20,0,0.39,0,0,1,0,0,1,0", "BITS.11111111,Fake weather box"]:
        stream.write(f"N0TLM>APRS,TCPIP*,qAC,FAKE::N0TLM    :{text}\r\n".encode())
    for call, dlat, dlon, symbol in INFRASTRUCTURE:
        line = f"{call}>APRS,TCPIP*,qAC,FAKE:{aprs_position(around[0] + dlat, around[1] + dlon, symbol, ' fake')}"
        stream.write((line + "\r\n").encode())


def serve(connection, number, around, count):
    stream = connection.makefile("rwb")
    fleet = stations(around, count)
    wx_rng = random.Random(7)
    try:
        stream.write(b"# fake-aprsis 1.0\r\n")
        stream.flush()
        login = stream.readline().decode("latin-1").strip()
        print(f"session {number}: {login}", flush=True)
        stream.write(f"# logresp N0TEST unverified, server FAKE{number}\r\n".encode())
        stream.flush()
        for station in fleet:  # everyone once at the start, so the map fills at once
            line = f"{station['call']}>APRS,TCPIP*,qAC,FAKE:{aprs_position(station['lat'], station['lon'], station['symbol'], weather(wx_rng) if station['symbol'][1] == '_' else ' simulated')}"
            stream.write((line + "\r\n").encode())
        stream.flush()
        beacon_infrastructure(stream, around)
        for i in range(100000):
            if i % 60 == 59:
                beacon_infrastructure(stream, around)
            # Heard direct, digipeated or from the internet, for the packet list's markers; now and then the same
            # packet again through a second IGate, as a duplicate.
            path = ["WIDE1-1,qAR,N0GATE-1", "N0DIGI-1*,WIDE2-1,qAR,N0GATE-1", "TCPIP*,qAC,FAKE"][i % 3]
            stream.write(f"N0SRC-{i % 9 + 1}>APRS,{path}:>fake packet {i}\r\n".encode())
            if i % 5 == 1:
                stream.write(f"N0SRC-{i % 9 + 1}>APRS,N0DIGI-2*,qAR,N0GATE-2:>fake packet {i}\r\n".encode())
            if around is not None and i % 10 == 5:
                # Telemetry every 10 s (F1.11): a slow sine, a ramp, and some bits.
                a1 = int(128 + 100 * math.sin(i / 30))
                stream.write(f"N0TLM>APRS,TCPIP*,qAC,FAKE:T#{i % 1000:03d},{a1:03d},{i % 256:03d},050,000,255,{i % 2}{(i // 2) % 2}100000\r\n".encode())
            if i % 15 == 7:
                # A malformed position now and then, for the packet list's parse issues.
                stream.write(f"N0BAD-{i % 3 + 1}>APRS,TCPIP*:!9999.99N/99999.99W- malformed {i}\r\n".encode())
            # Each moving station reports every 10 s, staggered, as real trackers do (about 70 packets a second
            # for 2,000 stations, under a full APRS-IS feed's rate).
            for n, station in enumerate(fleet):
                if station["moving"] and (n + i) % 10 == 0:
                    station["lat"] += 0.02 * math.cos(station["heading"])
                    station["lon"] += 0.02 * math.sin(station["heading"])
                    line = f"{station['call']}>APRS,{PATHS[n % len(PATHS)]}:{aprs_position(station['lat'], station['lon'], station['symbol'], weather(wx_rng) if station['symbol'][1] == '_' else ' moving')}"
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

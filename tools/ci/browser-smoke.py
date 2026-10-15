#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Browser smoke test for the docking shell (docs/features/F1.0-shell-and-docking.md, acceptance test 6).

Drives the published, trimmed web app in headless Firefox over WebDriver BiDi: drags a splitter, moves a panel tab to
another dock, floats a panel inside the page, reloads, and checks the saved layout each time, with no console errors.
These are the paths Dock's reflection bindings drive, which trimming could break. Screenshots go to OUT.

    python3 tools/ci/browser-smoke.py http://127.0.0.1:8080/ OUT

Needs Firefox and the Python `websockets` package. Positions assume the default layout in a 1280 × 800 viewport; each
drag tries a few heights for the panel headers, so small differences in fonts don't matter.
"""
import asyncio
import base64
import itertools
import json
import shutil
import subprocess
import sys
import tempfile

import websockets

URL, OUT = sys.argv[1], sys.argv[2]
PORT = 9343
KEY = "aprsmap.layout"
HEADER_HEIGHTS = [67, 62, 72, 58, 78]
# Avalonia's browser script asks for a permission Firefox doesn't know and doesn't catch the refusal; harmless.
ALLOWED_ERRORS = ["'window-management'"]


def drag(x1, y1, x2, y2, steps=12):
    actions = [
        {"type": "pointerMove", "x": x1, "y": y1},
        {"type": "pointerDown", "button": 0},
        {"type": "pause", "duration": 150},
    ]
    for i in range(1, steps + 1):
        actions.append(
            {"type": "pointerMove", "x": int(x1 + (x2 - x1) * i / steps), "y": int(y1 + (y2 - y1) * i / steps), "duration": 40}
        )
    actions += [{"type": "pause", "duration": 300}, {"type": "pointerUp", "button": 0}]
    return [{"type": "pointer", "id": "mouse", "parameters": {"pointerType": "mouse"}, "actions": actions}]


def dockables(node, owner=None):
    """Every object with an Id in Dock's saved layout, with the Id of the dock that holds it."""
    if isinstance(node, dict):
        if "Id" in node:
            yield node, owner
            owner = node.get("Id")
        for value in node.values():
            yield from dockables(value, owner)
    elif isinstance(node, list):
        for value in node:
            yield from dockables(value, owner)


def owner_of(layout, panel_id):
    return next((owner for node, owner in dockables(layout["dock"]) if node.get("Id") == panel_id and "Kind" in node), None)


def left_proportion(layout):
    return next((node.get("Proportion") for node, _ in dockables(layout["dock"]) if node.get("Id") == "left"), None)


def window_count(layout):
    windows = layout["dock"].get("Windows") or {}
    return len(windows.get("$values", windows) if isinstance(windows, dict) else windows)


async def main():
    profile = tempfile.mkdtemp(prefix="smoke-ff-")
    with open(f"{profile}/user.js", "w") as prefs:
        prefs.write('user_pref("browser.startup.page", 0);\nuser_pref("browser.startup.homepage", "about:blank");\n')
    firefox = subprocess.Popen(
        ["firefox", "--headless", "--no-remote", "--profile", profile, f"--remote-debugging-port={PORT}"],
        stdout=subprocess.DEVNULL,
        stderr=subprocess.DEVNULL,
    )
    errors, failures = [], []
    try:
        for _ in range(120):
            try:
                ws = await websockets.connect(f"ws://127.0.0.1:{PORT}/session", max_size=None)
                break
            except OSError:
                await asyncio.sleep(0.5)
        ids, pending = itertools.count(1), {}

        async def reader():
            async for raw in ws:
                message = json.loads(raw)
                if "id" in message and message["id"] in pending:
                    pending.pop(message["id"]).set_result(message)
                elif message.get("method") == "log.entryAdded":
                    entry = message["params"]
                    text = entry.get("text", "")
                    if entry.get("level") == "error" and not any(a in text for a in ALLOWED_ERRORS):
                        errors.append(text[:300])

        task = asyncio.create_task(reader())

        async def send(method, params):
            i = next(ids)
            pending[i] = asyncio.get_running_loop().create_future()
            await ws.send(json.dumps({"id": i, "method": method, "params": params}))
            message = await pending[i]
            if message.get("type") == "error":
                raise RuntimeError(message)
            return message["result"]

        async def evaluate(expression):
            result = await send("script.evaluate", {"expression": expression, "target": {"context": context}, "awaitPromise": False})
            return result["result"].get("value")

        async def shot(name):
            data = (await send("browsingContext.captureScreenshot", {"context": context}))["data"]
            with open(f"{OUT}/{name}.png", "wb") as file:
                file.write(base64.b64decode(data))

        async def stored():
            text = await evaluate(f"localStorage.getItem('{KEY}') || ''")
            return json.loads(text) if text else None

        async def started():
            for _ in range(180):
                if await evaluate("document.documentElement.dataset.appStarted || ''") == "true":
                    await asyncio.sleep(3)  # the first layout pass
                    return True
                await asyncio.sleep(1)
            return False

        async def until_saved(check):
            for _ in range(10):
                await asyncio.sleep(0.5)
                layout = await stored()
                if layout and check(layout):
                    return layout
            return None

        async def try_drags(name, points, check):
            for y in HEADER_HEIGHTS:
                x1, x2, y2 = points
                await send("input.performActions", {"context": context, "actions": drag(x1, y, x2, y2)})
                if await until_saved(check):
                    await shot(name)
                    return True
            await shot(name + "-failed")
            failures.append(name)
            return False

        await send("session.new", {"capabilities": {}})
        await send("session.subscribe", {"events": ["log.entryAdded"]})
        context = (await send("browsingContext.getTree", {}))["contexts"][0]["context"]
        await send("browsingContext.setViewport", {"context": context, "viewport": {"width": 1280, "height": 800}})
        await send("browsingContext.navigate", {"context": context, "url": URL, "wait": "complete"})
        if not await started():
            failures.append("the app didn't start")
        else:
            await shot("1-default")
            # The splitter between Stations and the map, at 20 % of the width.
            await send("input.performActions", {"context": context, "actions": drag(256, 300, 400, 300)})
            if not await until_saved(lambda l: (left_proportion(l) or 0.2) > 0.21):
                failures.append("splitter")
            await shot("2-splitter")
            # The Details tab, at the top of the right dock, onto the bottom dock.
            await try_drags("3-moved", (int(1280 * 0.72) + 30, 640, 700), lambda l: owner_of(l, "details") == "bottom")
            # The Stations tab into the middle of the map: it floats inside the page.
            await try_drags("4-floated", (30, 650, 260), lambda l: window_count(l) >= 1)
            before = await stored()
            await send("browsingContext.reload", {"context": context, "wait": "complete"})
            if not await started():
                failures.append("the app didn't start again after reloading")
            else:
                await shot("5-reloaded")
                after = await stored()
                if before and after and (owner_of(after, "details") != owner_of(before, "details") or window_count(after) != window_count(before)):
                    failures.append("the layout after reloading isn't the one saved")
        task.cancel()
        await ws.close()
    finally:
        firefox.terminate()
        firefox.wait(timeout=10)
        shutil.rmtree(profile, ignore_errors=True)
    for error in errors:
        print("console error:", error)
    for failure in failures:
        print("failed:", failure)
    if errors or failures:
        sys.exit(1)
    print("browser smoke test passed")


asyncio.run(main())

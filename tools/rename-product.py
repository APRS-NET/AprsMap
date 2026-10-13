#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Rename the product across the repository. See docs/branding.md.

Usage: rename-product.py OLD NEW [--apply] [--root DIR]

OLD and NEW are PascalCase names (e.g. AprsMap, AprsAtlas). Replaces the PascalCase, lower-case and upper-case forms
in file contents and in file and folder names. Without --apply it only reports what would change.
"""
import argparse
import pathlib
import subprocess
import sys

SKIP_DIRS = {".git", "LICENSES", "bin", "obj", "node_modules"}
SKIP_FILES = {"docs/reference/README.md"}  # the exposure log is a historical record


def forms(old: str, new: str) -> list[tuple[str, str]]:
    return [(old, new), (old.lower(), new.lower()), (old.upper(), new.upper())]


def is_text(path: pathlib.Path) -> bool:
    try:
        chunk = path.read_bytes()[:4096]
    except OSError:
        return False
    return b"\0" not in chunk


def walk(root: pathlib.Path):
    for p in sorted(root.rglob("*"), key=lambda q: len(q.parts), reverse=True):
        rel = p.relative_to(root)
        if any(part in SKIP_DIRS for part in rel.parts):
            continue
        yield p, rel


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("old")
    ap.add_argument("new")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--root", default=".")
    a = ap.parse_args()
    root = pathlib.Path(a.root).resolve()
    pairs = forms(a.old, a.new)
    edits = moves = 0

    for p, rel in walk(root):
        if p.is_file() and rel.as_posix() not in SKIP_FILES and is_text(p):
            text = p.read_text(encoding="utf-8", errors="surrogateescape")
            out = text
            for o, n in pairs:
                out = out.replace(o, n)
            if out != text:
                count = sum(text.count(o) for o, _ in pairs)
                print(f"edit  {rel}  ({count} occurrence(s))")
                edits += 1
                if a.apply:
                    p.write_text(out, encoding="utf-8", errors="surrogateescape")

    # Paths deepest first, so renaming a folder never invalidates a pending child path.
    for p, rel in walk(root):
        name = p.name
        for o, n in pairs:
            name = name.replace(o, n)
        if name != p.name:
            target = p.with_name(name)
            print(f"move  {rel}  ->  {target.relative_to(root)}")
            moves += 1
            if a.apply:
                r = subprocess.run(["git", "mv", str(p), str(target)], cwd=root, capture_output=True, text=True)
                if r.returncode != 0:
                    p.rename(target)

    # Remember the old slug so the app can migrate user data on first start (docs/branding.md, rule 5).
    props = root / "build" / "Branding.props"
    if props.exists():
        text = props.read_text(encoding="utf-8")
        old_slug = a.old.lower()
        start, end = text.find("<PreviousSlugs>"), text.find("</PreviousSlugs>")
        if start >= 0 and end > start:
            current = [s for s in text[start + len("<PreviousSlugs>") : end].split(";") if s]
            if old_slug not in current:
                print(f"edit  build/Branding.props  (PreviousSlugs += {old_slug})")
                if a.apply:
                    value = ";".join(current + [old_slug])
                    props.write_text(text[: start + len("<PreviousSlugs>")] + value + text[end:], encoding="utf-8")

    mode = "applied" if a.apply else "dry run"
    print(f"{mode}: {edits} file(s) edited, {moves} path(s) moved")
    return 0


if __name__ == "__main__":
    sys.exit(main())

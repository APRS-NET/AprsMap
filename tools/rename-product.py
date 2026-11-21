#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Rename the product across the repository. See docs/branding.md.

Usage: rename-product.py OLD NEW [--apply] [--root DIR] [--code-only] [--also OLD=NEW ...]

OLD and NEW are PascalCase names (e.g. ZedMap, AprsAtlas). Replaces the PascalCase, lower-case and upper-case forms
in file contents and in file and folder names. Without --apply it only reports what would change.

--code-only replaces only the PascalCase form (the code prefix: namespaces, projects, the solution), leaving the slug
(folders, executables, release assets, the container image) and the upper-case environment-variable prefix as they
are, and the links to the GitHub repository, which is renamed separately. It's the first step of a rename that keeps
every install's update path (docs/branding.md, "Renaming in two steps").

--also OLD=NEW adds a replacement, for a repository part-way through such a rename (the code prefix renamed, the slug
not yet). Repeat it for each.
"""
import argparse
import pathlib
import re
import subprocess
import sys

SKIP_DIRS = {".git", "LICENSES", "bin", "obj", "node_modules", ".idea"}
SKIP_FILES = {"docs/reference/README.md"}  # the exposure log is a historical record
# History, left as it was: the changelog's entries name the product as it was then.
CODE_ONLY_SKIP_FILES = {"CHANGELOG.md"}


def forms(old: str, new: str, code_only: bool) -> list[tuple[str, str]]:
    if code_only:
        return [(old, new)]
    return [(old, new), (old.lower(), new.lower()), (old.upper(), new.upper())]


def protected(old: str) -> list[str]:
    """Text kept as it is in --code-only: the repository's address, until the repository itself is renamed."""
    return [f"APRS-NET/{old}"]


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


# Kept in every mode: the installers' package id, which installed copies update by (build/Branding.props).
ALWAYS_KEPT = re.compile(r"<ProductPackageId>[^<]*</ProductPackageId>")


def replace(text: str, pairs: list[tuple[str, str]], keep: list[str]) -> str:
    # Kept text is swapped for placeholders first, so no replacement can touch it.
    keep = keep + sorted(set(ALWAYS_KEPT.findall(text)))
    holders = [(k, f"\x00KEEP{i}\x00") for i, k in enumerate(keep)]
    for k, h in holders:
        text = text.replace(k, h)
    for o, n in pairs:
        text = text.replace(o, n)
    for k, h in holders:
        text = text.replace(h, k)
    return text


def main() -> int:
    ap = argparse.ArgumentParser()
    ap.add_argument("old")
    ap.add_argument("new")
    ap.add_argument("--apply", action="store_true")
    ap.add_argument("--root", default=".")
    ap.add_argument("--code-only", action="store_true")
    ap.add_argument("--also", action="append", default=[], metavar="OLD=NEW")
    a = ap.parse_args()
    root = pathlib.Path(a.root).resolve()
    pairs = forms(a.old, a.new, a.code_only)
    for extra in a.also:
        o, sep, n = extra.partition("=")
        if not sep or not o:
            ap.error(f"--also takes OLD=NEW, not {extra!r}")
        pairs.append((o, n))
    keep = protected(a.old) if a.code_only else []
    skip_files = SKIP_FILES | (CODE_ONLY_SKIP_FILES if a.code_only else set())
    edits = moves = 0

    for p, rel in walk(root):
        if p.is_file() and rel.as_posix() not in skip_files and is_text(p):
            text = p.read_text(encoding="utf-8", errors="surrogateescape")
            out = replace(text, pairs, keep)
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

    # Remember the old slug so the app can migrate user data on first start (docs/branding.md, rule 5). Not when only
    # the code is renamed: the slug, and so where the data is, stays the same.
    props = root / "build" / "Branding.props"
    if props.exists() and not a.code_only:
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

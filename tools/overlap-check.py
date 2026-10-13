#!/usr/bin/env python3
# SPDX-License-Identifier: MIT
"""Flag word runs that our docs share with reference source text.

Usage: overlap-check.py SOURCE_DIR DOC [DOC ...] [--n 8]

SOURCE_DIR holds plain-text copies of the reference pages. Keep them outside the repository: they are other
people's copyrighted text. Prints every run of N or more consecutive words (case-insensitive, punctuation ignored)
that appears in both a doc and a source, and exits 1 if any are found. Runs made only of numbers, single letters and
common protocol tokens are ignored.
"""
import pathlib
import re
import sys

IGNORE = {"aprs", "is", "rf", "kiss", "tnc", "gps", "nmea", "ssid", "phg", "nws", "the", "a", "an", "of", "to", "and"}


def words(text: str) -> list[str]:
    return re.findall(r"[a-z0-9']+", text.lower())


def trivial(run: tuple[str, ...]) -> bool:
    return sum(1 for w in run if not (w.isdigit() or len(w) == 1 or w in IGNORE)) < 4


def main() -> int:
    args = sys.argv[1:]
    n = 8
    if "--n" in args:
        i = args.index("--n")
        n = int(args[i + 1])
        del args[i : i + 2]
    src_dir, docs = pathlib.Path(args[0]), args[1:]
    grams: dict[tuple[str, ...], str] = {}
    for f in sorted(src_dir.glob("*.txt")):
        w = words(f.read_text(errors="replace"))
        for i in range(len(w) - n + 1):
            grams.setdefault(tuple(w[i : i + n]), f.name)
    hits = 0
    for d in docs:
        w = words(pathlib.Path(d).read_text())
        i = 0
        while i <= len(w) - n:
            g = tuple(w[i : i + n])
            if g in grams and not trivial(g):
                j = i + n
                while j < len(w) and tuple(w[j - n + 1 : j + 1]) in grams:
                    j += 1
                print(f"{d}: \"{' '.join(w[i:j])}\"  (source: {grams[g]})")
                hits += 1
                i = j
            else:
                i += 1
    print(f"{hits} overlapping run(s) of {n}+ words")
    return 1 if hits else 0


if __name__ == "__main__":
    sys.exit(main())

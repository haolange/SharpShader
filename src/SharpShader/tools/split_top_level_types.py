#!/usr/bin/env python3
"""Split a C# file into one file per top-level type. Nested types stay with the parent."""
from __future__ import annotations

import pathlib
import re
import sys


TYPE_DECL = re.compile(
    r"^\s*(public|internal)\s+(?:(?:sealed|static|readonly|unsafe|partial)\s+)*"
    r"(enum|class|struct|interface|record(?:\s+struct|\s+class)?)\s+([A-Za-z_][A-Za-z0-9_]*)"
)


def strip_strings_and_comments(line: str) -> str:
    out = []
    i = 0
    while i < len(line):
        if line.startswith("//", i):
            break
        if line[i] == '"':
            out.append('"')
            i += 1
            while i < len(line):
                if line[i] == "\\":
                    i += 2
                    continue
                if line[i] == '"':
                    i += 1
                    break
                i += 1
            continue
        out.append(line[i])
        i += 1
    return "".join(out)


def leading_trivia_start(lines: list[str], index: int) -> int:
    start = index
    while start > 0:
        prev = lines[start - 1].rstrip()
        if prev == "" or prev.lstrip().startswith("///") or prev.lstrip().startswith("//") or prev.lstrip().startswith("["):
            start -= 1
            continue
        break
    return start


def split_file(path: pathlib.Path) -> None:
    raw = path.read_text(encoding="utf-8")
    lines = raw.splitlines()
    ns_index = next(i for i, line in enumerate(lines) if line.startswith("namespace "))
    header = "\n".join(lines[: ns_index + 1])
    uses_block_namespace = "{" in lines[ns_index] or (
        ns_index + 1 < len(lines) and lines[ns_index + 1].strip() == "{"
    )
    body_start = ns_index + 1
    if uses_block_namespace and body_start < len(lines) and lines[body_start].strip() == "{":
        body_start += 1
        header = "\n".join(lines[:body_start])

    types: list[tuple[str, int, int]] = []
    depth = 1 if uses_block_namespace else 0
    i = body_start
    while i < len(lines):
        stripped = strip_strings_and_comments(lines[i])
        match = TYPE_DECL.match(lines[i]) if depth == (1 if uses_block_namespace else 0) else None
        if match:
            name = match.group(3)
            start = leading_trivia_start(lines, i)
            type_depth = depth
            started = False
            j = i
            local_depth = 0
            while j < len(lines):
                local = strip_strings_and_comments(lines[j])
                for ch in local:
                    if ch == "{":
                        local_depth += 1
                        started = True
                    elif ch == "}":
                        local_depth -= 1
                if started and local_depth <= 0:
                    types.append((name, start, j))
                    i = j + 1
                    break
                if not started and local.rstrip().endswith(";"):
                    types.append((name, start, j))
                    i = j + 1
                    break
                j += 1
            else:
                raise SystemExit(f"Unclosed type {name} in {path}")
            continue
        for ch in stripped:
            if ch == "{":
                depth += 1
            elif ch == "}":
                depth -= 1
        i += 1

    if len(types) < 2:
        print(f"skip {path} types={len(types)}")
        return

    footer = "}" if uses_block_namespace else ""
    written: list[tuple[pathlib.Path, str]] = []
    for name, start, end in types:
        body = "\n".join(lines[start : end + 1]).rstrip()
        content = f"{header}\n{body}\n"
        if footer:
            content += f"{footer}\n"
        out = path.with_name(f"{name}.cs")
        written.append((out, content))
    if any(out.resolve() == path.resolve() for out, _ in written):
        backup = path.with_suffix(".cs.bak")
        path.replace(backup)
        source_removed = backup
    else:
        source_removed = path
    try:
        for out, content in written:
            out.write_text(content, encoding="utf-8", newline="\n")
            print(f"wrote {out}")
        if source_removed.exists() and source_removed.resolve() != path.with_name(written[0][0].name).resolve():
            source_removed.unlink()
            print(f"removed {source_removed}")
        elif source_removed.suffix == ".bak":
            source_removed.unlink()
            print(f"removed original {path.name}")
    except Exception:
        if source_removed.suffix == ".bak" and not path.exists():
            source_removed.replace(path)
        raise


def main() -> None:
    if len(sys.argv) < 2:
        raise SystemExit("usage: split_top_level_types.py <file> [<file>...]")
    for arg in sys.argv[1:]:
        split_file(pathlib.Path(arg))


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Extract method ranges from a C# type into sibling partial files.

Usage:
  extract_partials.py <source.cs> <ClassName> <Dest.cs:start-end> [<Dest.cs:start-end>...]

Line numbers are 1-based inclusive and refer to the original file.
Ranges are extracted in one pass so later ranges stay valid.
"""

from __future__ import annotations

import pathlib
import re
import sys


CLASS_PATTERN = re.compile(
    r"^(\s*)((?:public|internal|private|protected|file)\s+)"
    r"((?:static\s+|sealed\s+|unsafe\s+|abstract\s+|readonly\s+|partial\s+)*)"
    r"(class|struct|record(?:\s+struct)?|enum)\s+"
    r"(\w+)"
)


def parse_span(span: str) -> tuple[int, int]:
    start_text, end_text = span.split("-", 1)
    start = int(start_text)
    end = int(end_text)
    if start < 1 or end < start:
        raise SystemExit(f"invalid span {span}")
    return start, end


def parse_range(spec: str) -> tuple[str, list[tuple[int, int]]]:
    dest, spans_text = spec.rsplit(":", 1)
    spans = [parse_span(item) for item in spans_text.split(",")]
    return dest, spans


def make_partial(header_lines: list[str], class_name: str) -> list[str]:
    updated: list[str] = []
    found = False
    for line in header_lines:
        match = CLASS_PATTERN.match(line)
        if match and match.group(5) == class_name and not found:
            found = True
            indent, visibility, modifiers, kind, name = match.groups()
            tokens = modifiers.split()
            if "partial" not in tokens:
                tokens.append("partial")
            modifier_text = (" ".join(tokens) + " ") if tokens else ""
            suffix = line[match.end() :]
            line = f"{indent}{visibility}{modifier_text}{kind} {name}{suffix}"
        updated.append(line)
    if not found:
        raise SystemExit(f"did not find type {class_name} in header")
    return updated


def split_header(lines: list[str], class_name: str) -> tuple[list[str], int]:
    for index, line in enumerate(lines):
        match = CLASS_PATTERN.match(line)
        if match and match.group(5) == class_name:
            # include the opening brace line that follows the declaration
            brace_index = index
            while brace_index < len(lines) and "{" not in lines[brace_index]:
                brace_index += 1
            if brace_index >= len(lines):
                raise SystemExit(f"{class_name} has no opening brace")
            return lines[: brace_index + 1], brace_index
    raise SystemExit(f"did not find type {class_name}")


def footer_for(header: list[str]) -> list[str]:
    uses_block_namespace = any(line.startswith("namespace ") for line in header)
    uses_file_scoped_namespace = any(
        line.startswith("namespace ") and line.rstrip().endswith(";")
        for line in header
    )
    if uses_block_namespace and not uses_file_scoped_namespace:
        return ["}", "}"]
    return ["}"]


def main() -> None:
    if len(sys.argv) < 4:
        raise SystemExit(
            "usage: extract_partials.py <source.cs> <ClassName> "
            "<Dest.cs:start-end> [<Dest.cs:start-end>...]"
        )

    source = pathlib.Path(sys.argv[1])
    class_name = sys.argv[2]
    ranges = [parse_range(item) for item in sys.argv[3:]]
    lines = source.read_text(encoding="utf-8").splitlines()
    header, _ = split_header(lines, class_name)
    header = make_partial(header, class_name)
    footer = footer_for(header)

    occupied: set[int] = set()
    extracts: list[tuple[pathlib.Path, list[str]]] = []
    for dest_name, spans in ranges:
        body: list[str] = []
        for start, end in spans:
            if end > len(lines):
                raise SystemExit(f"{dest_name} end {end} exceeds {len(lines)} lines")
            for line_no in range(start, end + 1):
                if line_no in occupied:
                    raise SystemExit(f"overlapping range at line {line_no}")
                occupied.add(line_no)
            if body:
                body.append("")
            body.extend(lines[start - 1 : end])
        while body and body[-1] == "":
            body.pop()
        dest = source.with_name(dest_name)
        content_lines = header + [""] + body + footer
        extracts.append((dest, content_lines))

    remaining: list[str] = []
    for index, line in enumerate(lines, start=1):
        if index not in occupied:
            remaining.append(line)

    remaining_text = "\n".join(make_partial(remaining, class_name)).rstrip() + "\n"
    source.write_text(remaining_text, encoding="utf-8", newline="\n")
    print(f"updated {source}")
    for dest, content_lines in extracts:
        dest.write_text("\n".join(content_lines).rstrip() + "\n", encoding="utf-8", newline="\n")
        print(f"wrote {dest}")


if __name__ == "__main__":
    main()

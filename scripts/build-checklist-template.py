#!/usr/bin/env python3
"""Extract the approved inspector checklist working layer from XLSX."""

from __future__ import annotations

import argparse
import hashlib
import json
import re
from pathlib import Path

import openpyxl


SPACE = re.compile(r"[ \t]+")
SECTION_CODES = {
    "общие вопросы": "1",
    "атмосферного воздуха": "2",
    "водных объектов": "3",
    "обращения с отходами": "4",
    "земельный": "5",
    "геологическим изучением": "6",
    "животного мира": "7",
}


def clean(value: object) -> str:
    if value is None:
        return ""
    lines = [SPACE.sub(" ", line).strip() for line in str(value).replace("_x0004_", " ").splitlines()]
    return "\n".join(line for line in lines if line)


def section_code(title: str) -> str | None:
    folded = title.casefold()
    return next((code for marker, code in SECTION_CODES.items() if marker in folded), None)


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path)
    parser.add_argument("output", type=Path)
    args = parser.parse_args()

    sheet = openpyxl.load_workbook(args.source, read_only=True, data_only=True).active
    current_section = ""
    current_code: str | None = None
    items: list[dict[str, object]] = []
    for row in sheet.iter_rows(values_only=True):
        number = clean(row[0])
        title = clean(row[1]) if len(row) > 1 else ""
        basis = clean(row[2]) if len(row) > 2 else ""
        if number and not title and not basis:
            detected = section_code(number)
            if detected:
                current_section, current_code = number, detected
        if re.fullmatch(r"\d+", number) and title and basis:
            if current_code is None:
                raise ValueError(f"Item {number}: section is not recognized")
            digest = hashlib.sha256(f"{current_code}\0{title}\0{basis}".encode("utf-8")).hexdigest()[:24]
            items.append({
                "id": digest,
                "position": int(number),
                "sectionCode": current_code,
                "section": current_section,
                "title": title,
                "basis": basis,
            })

    if len(items) != 235 or [item["position"] for item in items] != list(range(1, 236)):
        raise ValueError("Approved template must contain 235 sequential checklist items")
    args.output.parent.mkdir(parents=True, exist_ok=True)
    with args.output.open("w", encoding="utf-8", newline="\n") as output:
        for item in items:
            output.write(json.dumps(item, ensure_ascii=False, separators=(",", ":")) + "\n")
    manifest = {
        "sourceFile": args.source.name,
        "sourceSha256": hashlib.sha256(args.source.read_bytes()).hexdigest(),
        "templateItems": len(items),
        "sectionCounts": {code: sum(item["sectionCode"] == code for item in items) for code in "1234567"},
    }
    args.output.with_name(f"{args.output.stem}.manifest.json").write_text(
        json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8")
    print(f"Checklist template built: {len(items)} items.")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""Build reviewed and review-only checklist links from immutable JSONL sources."""

from __future__ import annotations

import hashlib
import json
import math
import re
import sys
from collections import Counter
from pathlib import Path

ALGORITHM_VERSION = "legal-lexical-v2"
WORD_RE = re.compile(r"[0-9a-zа-яё]+", re.IGNORECASE)
LAW_RE = re.compile(r"№\s*(\d+)(?:-[а-яa-z]+)?", re.IGNORECASE)
ARTICLE_RE = re.compile(r"(?:стат(?:ья|ьи|ье|ей|ью)|ст\.)\s*(\d+(?:\.\d+)*)", re.IGNORECASE)
POINT_RE = re.compile(r"(?:пункт(?:ы|а|е|ом|ов)?|п\.)\s*([\d.,\s]+)", re.IGNORECASE)
PART_RE = re.compile(r"(?:част(?:ь|и|ью|ей)|ч\.)\s*([\d.,\s]+)", re.IGNORECASE)
STOP = {"проверить", "соблюдение", "требование", "требований", "наличие", "порядок", "объект", "объекта", "российской", "федерации", "федерального", "закона", "пункт", "пункты", "статья", "статьи", "должен", "должна", "осуществление"}


def read_jsonl(path: Path) -> list[dict]:
    return [json.loads(line) for line in path.read_text(encoding="utf-8").splitlines() if line.strip()]


def sha256(path: Path) -> str:
    return hashlib.sha256(path.read_bytes()).hexdigest()


def normalized(value: str) -> str:
    return " ".join(WORD_RE.findall(value.lower().replace("ё", "е")))


def tokens(value: str) -> Counter[str]:
    return Counter(word for word in WORD_RE.findall(value.lower().replace("ё", "е")) if len(word) >= 4 and word not in STOP)


def cosine(left: Counter[str], right: Counter[str]) -> float:
    common = sum(left[key] * right[key] for key in left.keys() & right.keys())
    denominator = math.sqrt(sum(value * value for value in left.values()) * sum(value * value for value in right.values()))
    return common / denominator if denominator else 0.0


def legal_signature(value: str) -> dict[str, set[str]]:
    normalized_value = value.lower().replace("ё", "е")
    laws = set(LAW_RE.findall(normalized_value))
    articles = set(ARTICLE_RE.findall(normalized_value))
    points: set[str] = set()
    for group in POINT_RE.findall(normalized_value):
        points.update(re.findall(r"\d+(?:\.\d+)*", group))
    parts: set[str] = set()
    for group in PART_RE.findall(normalized_value):
        parts.update(re.findall(r"\d+(?:\.\d+)*", group))
    return {"laws": laws, "articles": articles, "points": points, "parts": parts}


def overlap(left: set[str], right: set[str]) -> bool:
    return bool(left and right and left.intersection(right))


def score(item: dict, requirement: dict) -> tuple[float, str]:
    item_legal = legal_signature(item.get("basis", ""))
    requirement_legal = legal_signature(requirement.get("basis", ""))
    law = overlap(item_legal["laws"], requirement_legal["laws"])
    article = overlap(item_legal["articles"], requirement_legal["articles"])
    point = overlap(item_legal["points"], requirement_legal["points"])
    part = overlap(item_legal["parts"], requirement_legal["parts"])
    lexical = cosine(tokens(item.get("title", "") + " " + item.get("basis", "")), tokens(requirement.get("requirement", "") + " " + requirement.get("basis", "")))
    value = (0.40 if law else 0.0) + (0.30 if article else 0.0) + (0.10 if point else 0.0) + (0.10 if part else 0.0) + 0.25 * lexical
    explanation_parts = [name for name, present in (("нормативный акт", law), ("статья", article), ("пункт", point), ("часть", part)) if present]
    explanation_parts.append(f"лексическое сходство {lexical:.3f}")
    return round(min(value, 1.0), 6), ", ".join(explanation_parts)


def classifier_codes(requirement: dict, section_code: str) -> list[str]:
    codes = [str(code) for code in requirement.get("classifierCodes", []) if str(code).split(".")[0] == section_code]
    return sorted(set(codes), key=lambda code: [int(part) for part in code.split(".")])


def main() -> int:
    if len(sys.argv) != 5:
        raise SystemExit("usage: build-checklist-item-catalog.py REQUIREMENTS TEMPLATE OVERRIDES OUTPUT")
    requirement_path, template_path, override_path, output_path = map(Path, sys.argv[1:])
    review_path = output_path.with_name("checklist-item-review.jsonl")
    manifest_path = output_path.with_name("checklist-item-catalog.manifest.json")
    requirements = read_jsonl(requirement_path)
    items = read_jsonl(template_path)
    overrides = json.loads(override_path.read_text(encoding="utf-8"))
    requirements_by_id = {item["id"]: item for item in requirements}
    approved: list[dict] = []
    review: list[dict] = []

    for item in items:
        section_code = str(item["sectionCode"])
        candidates = [requirement for requirement in requirements if classifier_codes(requirement, section_code)]
        ranked = sorted(((score(item, requirement), requirement) for requirement in candidates), key=lambda pair: (-pair[0][0], pair[1]["id"]))
        override_ids = overrides.get("approved", {}).get(item["id"], [])
        selected: list[dict] = []
        provenance = ""
        explanation = ""
        link_score = 0.0
        if override_ids:
            selected = [requirements_by_id[requirement_id] for requirement_id in override_ids]
            provenance = "reviewed-override"
            explanation = "Связь утверждена явным экспертным override."
            link_score = 1.0
        elif ranked:
            top_score, top_explanation = ranked[0][0]
            second_score = ranked[1][0][0] if len(ranked) > 1 else 0.0
            top_legal = legal_signature(item.get("basis", ""))
            requirement_legal = legal_signature(ranked[0][1].get("basis", ""))
            strong_legal = overlap(top_legal["laws"], requirement_legal["laws"]) and overlap(top_legal["articles"], requirement_legal["articles"])
            clause_compatible = all(not top_legal[key] or overlap(top_legal[key], requirement_legal[key]) for key in ("points", "parts"))
            if strong_legal and clause_compatible and top_score >= 0.78 and top_score - second_score >= 0.035:
                selected = [ranked[0][1]]
                provenance = ALGORITHM_VERSION
                explanation = f"Уникальная связь: {top_explanation}; отрыв {top_score - second_score:.3f}."
                link_score = top_score

        if selected:
            codes = sorted({code for requirement in selected for code in classifier_codes(requirement, section_code)}, key=lambda code: [int(part) for part in code.split(".")])
            approved.append({
                **item,
                "classifierCodes": codes,
                "requirementIds": [requirement["id"] for requirement in selected],
                "status": "approved",
                "provenance": provenance,
                "linkScore": link_score,
                "linkExplanation": explanation,
            })
        else:
            review.append({
                **item,
                "status": "needs_review",
                "candidates": [{"requirementId": requirement["id"], "score": candidate_score, "explanation": candidate_explanation} for ((candidate_score, candidate_explanation), requirement) in ranked[:5]],
            })

    def write_jsonl(path: Path, rows: list[dict]) -> None:
        path.write_text("".join(json.dumps(row, ensure_ascii=False, separators=(",", ":")) + "\n" for row in rows), encoding="utf-8", newline="\n")

    write_jsonl(output_path, approved)
    write_jsonl(review_path, review)
    selected_requirement_ids = {requirement_id for item in approved for requirement_id in item["requirementIds"]}
    per_section = {code: sum(1 for item in approved if item["sectionCode"] == code) for code in sorted({str(item["sectionCode"]) for item in items})}
    manifest = {
        "algorithmVersion": ALGORITHM_VERSION,
        "sources": {"requirements": sha256(requirement_path), "template": sha256(template_path), "overrides": sha256(override_path)},
        "thresholds": {"minimumScore": 0.78, "minimumMargin": 0.035, "requiresLawAndArticle": True, "requiresMatchingClauseWhenSpecified": True},
        "counts": {"templateItems": len(items), "approvedItems": len(approved), "needsReviewItems": len(review), "linkedRequirements": len(selected_requirement_ids), "uncoveredRequirements": len(requirements) - len(selected_requirement_ids)},
        "approvedBySection": per_section,
    }
    manifest_path.write_text(json.dumps(manifest, ensure_ascii=False, indent=2) + "\n", encoding="utf-8", newline="\n")
    print(json.dumps(manifest["counts"], ensure_ascii=False))
    return 0


if __name__ == "__main__":
    raise SystemExit(main())

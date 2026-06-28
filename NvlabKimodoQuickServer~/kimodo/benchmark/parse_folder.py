#!/usr/bin/env python3
# SPDX-FileCopyrightText: Copyright (c) 2026 NVIDIA CORPORATION & AFFILIATES. All rights reserved.
# SPDX-License-Identifier: Apache-2.0

"""
Step (5) of evaluation pipeline.

Validate testcase result JSONs and aggregate benchmark rows.

Expected testsuite layout (aligned with evaluate_folder output):

    <root>/
    ├── <split>/                    # e.g. content, repetition
    │   ├── text2motion/            # text-following eval
    │   │   ├── overview/           # or timeline_single, timeline_multi
    │   │   │   └── <testcase>.json
    │   │   └── ...
    │   └── <category>/             # constraints_withtext, constraints_notext
    │       └── .../                 # optional subdirs, e.g. root, fullbody
    │           └── <testcase>/
    │           └── <testcase>.json

Samples are discovered via rglob('meta.json') with motion.npz and gt_motion.npz in the same dir.
Testcase dir = parent of a sample dir. Result file = testcase_dir.parent / f"{testcase_dir.name}.json".
"""

from __future__ import annotations

import argparse
import json
from collections import defaultdict
from pathlib import Path
from typing import Any

SPLITS = ("content", "repetition")
TEXT_FOLLOWING_CATEGORIES = ("overview", "timeline_single", "timeline_multi")
CONSTRAINTS_CATEGORIES = ("constraints_withtext", "constraints_notext")
ROW_CATEGORIES = TEXT_FOLLOWING_CATEGORIES + CONSTRAINTS_CATEGORIES


def _discover_sample_dirs(root: Path) -> list[Path]:
    sample_dirs: list[Path] = []
    for meta_path in root.rglob("meta.json"):
        sample_dir = meta_path.parent
        if (sample_dir / "motion.npz").is_file() and (sample_dir / "gt_motion.npz").is_file():
            sample_dirs.append(sample_dir)
    return sorted(set(sample_dirs))


def _discover_testcase_dirs(root: Path) -> list[Path]:
    sample_dirs = _discover_sample_dirs(root)
    return sorted({sample_dir.parent for sample_dir in sample_dirs})


def _expected_result_path(testcase_dir: Path) -> Path:
    return testcase_dir.parent / f"{testcase_dir.name}.json"


def _parse_testcase_key(root: Path, testcase_dir: Path) -> tuple[str, str]:
    rel_parts = testcase_dir.relative_to(root).parts
    if len(rel_parts) < 2:
        raise ValueError(f"Unexpected testcase path shape: {testcase_dir} (relative: {'/'.join(rel_parts)})")
    split = rel_parts[0]
    if split not in SPLITS:
        raise ValueError(f"Unknown split '{split}' for testcase {testcase_dir}")
    if len(rel_parts) >= 3 and rel_parts[1] == "text2motion":
        category = rel_parts[2]
        if category not in TEXT_FOLLOWING_CATEGORIES:
            raise ValueError(f"Unknown text-following category '{category}' for testcase {testcase_dir}")
    else:
        category = rel_parts[1]
        if category not in CONSTRAINTS_CATEGORIES:
            raise ValueError(f"Unknown category '{category}' for testcase {testcase_dir}")
    return split, category


def _accumulate_weighted(acc: dict[str, float], metric_dict: dict[str, Any], weight: float) -> None:
    for metric_name, value in metric_dict.items():
        if isinstance(value, (int, float)):
            acc[metric_name] = acc.get(metric_name, 0.0) + float(value) * weight


def _to_averages(weighted_sum: dict[str, float], total_weight: float) -> dict[str, float]:
    if total_weight <= 0:
        return {}
    return {k: v / total_weight for k, v in sorted(weighted_sum.items())}


def _load_result_row(
    result_path: Path,
) -> tuple[float, dict[str, Any], dict[str, Any], dict[str, Any]]:
    payload = json.loads(result_path.read_text(encoding="utf-8"))
    num_motions = float(payload.get("num_motions", 1))
    per_motion_mean_gen = payload.get("per_motion_mean_gen") or payload.get("per_motion_mean", {})
    per_motion_mean_gt = payload.get("per_motion_mean_gt") or {}
    tmr = payload.get("tmr") or {}
    if not isinstance(per_motion_mean_gen, dict):
        raise ValueError(f"'per_motion_mean_gen' / 'per_motion_mean' is not a dict in {result_path}")
    if not isinstance(per_motion_mean_gt, dict):
        raise ValueError(f"'per_motion_mean_gt' is not a dict in {result_path}")
    if not isinstance(tmr, dict):
        raise ValueError(f"'tmr' is not a dict in {result_path}")
    return num_motions, per_motion_mean_gen, per_motion_mean_gt, tmr


# Display labels for table rows (paper-style).
TEXT_FOLLOWING_ROW_LABELS = {
    "overview": "Overview",
    "timeline_single": "Timeline single",
    "timeline_multi": "Timeline multi",
}
CONSTRAINTS_ROW_LABELS = {
    "constraints_withtext": "Constraints with text",
    "constraints_notext": "Constraints without text",
}

# Meters to cm for constraint position metrics.
M_TO_CM = 100.0


def _table_value(val: float | None) -> float | str | None:
    """Return value for JSON table; use None for missing (omit or serialize as null)."""
    if val is None:
        return None
    if isinstance(val, (int, float)) and (val != val or val == float("inf")):  # nan or inf
        return None
    return val


def _build_tables(
    row_acc: dict[tuple[str, str], dict[str, Any]],
) -> dict[str, dict[str, list[dict[str, Any]]]]:
    """Build text_following and constraints tables per split for paper-style output."""
    tables: dict[str, dict[str, list[dict[str, Any]]]] = {}
    for split in SPLITS:
        tables[split] = {"text_following": [], "constraints": []}

        # Text-following table: Overview, Timeline single, Timeline multi.
        for category in TEXT_FOLLOWING_CATEGORIES:
            acc = row_acc[(split, category)]
            per_motion_gen = _to_averages(acc["per_motion_mean_weighted_sum"], acc["num_motions"])
            per_motion_gt = _to_averages(acc["per_motion_mean_gt_weighted_sum"], acc["num_motions"])
            tmr_avg = _to_averages(acc["tmr_weighted_sum"], acc["tmr_weight"]) if acc["tmr_weight"] > 0 else {}
            r03_gen = tmr_avg.get("TMR/t2m_R/R03")
            r03_gt = tmr_avg.get("TMR/t2m_gt_R/R03")
            fid_gen_text = tmr_avg.get("TMR/FID/gen_text")
            fid_gt_text = tmr_avg.get("TMR/FID/gt_text")
            fid_gen_gt = tmr_avg.get("TMR/FID/gen_gt")
            # Skate is velocity in m/s; convert to cm/s for display.
            skate_gen = per_motion_gen.get("foot_skate_from_pred_contacts")
            skate_gt = per_motion_gt.get("foot_skate_from_pred_contacts")
            contact_gen = per_motion_gen.get("foot_contact_consistency")
            contact_gt = per_motion_gt.get("foot_contact_consistency")
            row_label = TEXT_FOLLOWING_ROW_LABELS[category]
            tables[split]["text_following"].append(
                {
                    "row": row_label,
                    "R@3 (gen)": _table_value(r03_gen),
                    "R@3 (GT)": _table_value(r03_gt),
                    "FID gen-text": _table_value(fid_gen_text),
                    "FID GT-text": _table_value(fid_gt_text),
                    "FID gen-GT": _table_value(fid_gen_gt),
                    "Skate (gen, cm/s)": _table_value(skate_gen * 100.0 if skate_gen is not None else None),
                    "Skate (GT, cm/s)": _table_value(skate_gt * 100.0 if skate_gt is not None else None),
                    "Contact (gen)": _table_value(contact_gen),
                    "Contact (GT)": _table_value(contact_gt),
                }
            )

        # Constraints table: Constraints with text, Constraints without text.
        for category in CONSTRAINTS_CATEGORIES:
            acc = row_acc[(split, category)]
            per_motion_gen = _to_averages(acc["per_motion_mean_weighted_sum"], acc["num_motions"])
            per_motion_gt = _to_averages(acc["per_motion_mean_gt_weighted_sum"], acc["num_motions"])
            row_label = CONSTRAINTS_ROW_LABELS[category]
            row_dict: dict[str, Any] = {
                "row": row_label,
                "Full-Body Pos (gen, cm)": _table_value(
                    per_motion_gen.get("constraint_fullbody_keyframe") * M_TO_CM
                    if per_motion_gen.get("constraint_fullbody_keyframe") is not None
                    else None
                ),
                "Full-Body Pos (GT, cm)": _table_value(
                    per_motion_gt.get("constraint_fullbody_keyframe") * M_TO_CM
                    if per_motion_gt.get("constraint_fullbody_keyframe") is not None
                    else None
                ),
                "End-Effector Pos (gen, cm)": _table_value(
                    per_motion_gen.get("constraint_end_effector") * M_TO_CM
                    if per_motion_gen.get("constraint_end_effector") is not None
                    else None
                ),
                "End-Effector Pos (GT, cm)": _table_value(
                    per_motion_gt.get("constraint_end_effector") * M_TO_CM
                    if per_motion_gt.get("constraint_end_effector") is not None
                    else None
                ),
                "End-Effector Rot (deg)": None,  # Not implemented in metrics.
                "2D Root Pos (gen, cm)": _table_value(
                    per_motion_gen.get("constraint_root2d_err") * M_TO_CM
                    if per_motion_gen.get("constraint_root2d_err") is not None
                    else None
                ),
                "2D Root Pos (GT, cm)": _table_value(
                    per_motion_gt.get("constraint_root2d_err") * M_TO_CM
                    if per_motion_gt.get("constraint_root2d_err") is not None
                    else None
                ),
                "2D Pelvis Pos@95% (gen, cm)": _table_value(
                    per_motion_gen.get("constraint_root2d_err_p95") * M_TO_CM
                    if per_motion_gen.get("constraint_root2d_err_p95") is not None
                    else None
                ),
                "2D Pelvis Pos@95% (GT, cm)": _table_value(
                    per_motion_gt.get("constraint_root2d_err_p95") * M_TO_CM
                    if per_motion_gt.get("constraint_root2d_err_p95") is not None
                    else None
                ),
            }
            tables[split]["constraints"].append(row_dict)

    return tables


def _fmt_md(val: float | None, decimals: int) -> str:
    """Format a numeric value for a markdown cell, or '-' for None/NaN."""
    if val is None:
        return "-"
    if isinstance(val, float) and (val != val or val == float("inf")):
        return "-"
    return f"{val:.{decimals}f}"


def _print_tf_formatted_md(
    splits_data: list[tuple[str, list[dict[str, Any]]]],
    title: str,
) -> None:
    """Print text-following table in markdown, mirroring the terminal layout."""
    groups = ["Overview", "Timeline single", "Timeline multi"]
    specs: list[tuple[str, int]] = [
        ("R@3\u2191", 2),
        ("FID\u2193", 3),
        ("Skate\u2193", 3),
        ("Contact\u2191", 3),
    ]
    gt_keys = ["R@3 (GT)", None, "Skate (GT, cm/s)", "Contact (GT)"]
    gen_keys = ["R@3 (gen)", "FID gen-GT", "Skate (gen, cm/s)", "Contact (gen)"]
    gt_defaults: list[float | None] = [None, 0.0, None, None]

    headers = [""]
    for g in groups:
        for hdr, _ in specs:
            headers.append(f"{g} {hdr}")

    print(f"\n### {title}\n")
    print("| " + " | ".join(headers) + " |")
    print("| " + " | ".join("---" for _ in headers) + " |")

    for split_label, rows in splits_data:
        for row_type, keys, defaults in [
            ("Ground Truth", gt_keys, gt_defaults),
            ("Method", gen_keys, [None] * len(specs)),
        ]:
            cells = [f"**{split_label}** {row_type}"]
            for row in rows:
                for j, (_, dec) in enumerate(specs):
                    key = keys[j]
                    val = defaults[j] if key is None else row.get(key)
                    cells.append(_fmt_md(val, dec))
            print("| " + " | ".join(cells) + " |")

    print()


def _print_c_formatted_md(
    splits_data: list[tuple[str, list[dict[str, Any]]]],
    title: str,
) -> None:
    """Print constraints table in markdown, mirroring the terminal layout."""
    groups = ["With text", "Without text"]
    specs: list[tuple[str, int]] = [
        ("FB Pos\u2193", 3),
        ("EE Pos\u2193", 3),
        ("EE Rot\u2193", 3),
        ("2D Root\u2193", 3),
        ("Pelvis@95%", 2),
    ]
    gt_keys = [
        "Full-Body Pos (GT, cm)",
        "End-Effector Pos (GT, cm)",
        "End-Effector Rot (deg)",
        "2D Root Pos (GT, cm)",
        "2D Pelvis Pos@95% (GT, cm)",
    ]
    gen_keys = [
        "Full-Body Pos (gen, cm)",
        "End-Effector Pos (gen, cm)",
        "End-Effector Rot (deg)",
        "2D Root Pos (gen, cm)",
        "2D Pelvis Pos@95% (gen, cm)",
    ]

    headers = [""]
    for g in groups:
        for hdr, _ in specs:
            headers.append(f"{g} {hdr}")

    print(f"\n### {title}\n")
    print("| " + " | ".join(headers) + " |")
    print("| " + " | ".join("---" for _ in headers) + " |")

    for split_label, rows in splits_data:
        for row_type, keys in [("Ground Truth", gt_keys), ("Method", gen_keys)]:
            cells = [f"**{split_label}** {row_type}"]
            for row in rows:
                for j, (_, dec) in enumerate(specs):
                    cells.append(_fmt_md(row.get(keys[j]), dec))
            print("| " + " | ".join(cells) + " |")

    print()


def _print_formatted_gt_method_md(
    tables: dict[str, dict[str, list[dict[str, Any]]]],
) -> None:
    """Print combined tables in markdown format, mirroring the terminal layout."""
    tf_splits: list[tuple[str, list[dict[str, Any]]]] = []
    c_splits: list[tuple[str, list[dict[str, Any]]]] = []
    for split in SPLITS:
        split_tables = tables.get(split, {})
        tf_rows = split_tables.get("text_following", [])
        c_rows = split_tables.get("constraints", [])
        if tf_rows and len(tf_rows) == 3:
            tf_splits.append((split.capitalize(), tf_rows))
        if c_rows and len(c_rows) == 2:
            c_splits.append((split.capitalize(), c_rows))

    if tf_splits:
        _print_tf_formatted_md(tf_splits, "Text-Following Evaluation")
    if c_splits:
        _print_c_formatted_md(c_splits, "Constrained Evaluation")


def _fmt(val: float | None, decimals: int, width: int) -> str:
    """Format a numeric value right-aligned to *width*, or '-' for None."""
    if val is None:
        return f"{'-':>{width}}"
    return f"{val:>{width}.{decimals}f}"


def _print_grouped_rows(
    label: str,
    rows: list[dict[str, Any]],
    specs: list[tuple[str, int, int]],
    keys: list[str],
    mw: int,
    sep: str,
) -> None:
    """Print one data row across all column groups."""
    parts = [f"{label:<{mw}}"]
    for i, row in enumerate(rows):
        if i:
            parts.append(sep)
        for j, (_, dec, w) in enumerate(specs):
            parts.append(_fmt(row.get(keys[j]), dec, w))
    print("".join(parts))


def _print_tf_formatted(
    splits_data: list[tuple[str, list[dict[str, Any]]]],
    title: str,
) -> None:
    """Print text-following table with Overview / Timeline single / Timeline multi groups.

    *splits_data* is a list of ``(split_label, category_rows)`` tuples so
    that content and repetition splits appear as separate row-pairs inside
    one table.
    """
    groups = ["Overview", "Timeline single", "Timeline multi"]
    specs: list[tuple[str, int, int]] = [
        ("R@3\u2191", 2, 7),
        ("FID\u2193", 3, 7),
        ("Skate\u2193", 3, 9),
        ("Contact\u2191", 3, 10),
    ]
    gt_keys = ["R@3 (GT)", None, "Skate (GT, cm/s)", "Contact (GT)"]
    gen_keys = ["R@3 (gen)", "FID gen-GT", "Skate (gen, cm/s)", "Contact (gen)"]
    gt_defaults: list[float | None] = [None, 0.0, None, None]

    mw = 16
    gw = sum(s[2] for s in specs)
    sep = " | "
    total_w = mw + len(groups) * gw + (len(groups) - 1) * len(sep)

    print(f"\n{title:^{total_w}}")
    print("=" * total_w)

    parts: list[str] = [" " * mw]
    for i, g in enumerate(groups):
        if i:
            parts.append(sep)
        parts.append(g.center(gw))
    print("".join(parts))

    parts = [f"{'':<{mw}}"]
    for i in range(len(groups)):
        if i:
            parts.append(sep)
        for hdr, _, w in specs:
            parts.append(f"{hdr:>{w}}")
    print("".join(parts))

    parts = ["\u2500" * mw]
    for i in range(len(groups)):
        if i:
            parts.append("\u2500\u253c\u2500")
        parts.append("\u2500" * gw)
    print("".join(parts))

    for si, (split_label, rows) in enumerate(splits_data):
        tag = f"\u2500\u2500 {split_label} "
        print(tag + "\u2500" * (total_w - len(tag)))

        parts = [f"{'Ground Truth':<{mw}}"]
        for i, row in enumerate(rows):
            if i:
                parts.append(sep)
            for j, (_, dec, w) in enumerate(specs):
                key = gt_keys[j]
                val = gt_defaults[j] if key is None else row.get(key)
                parts.append(_fmt(val, dec, w))
        print("".join(parts))

        _print_grouped_rows("Method", rows, specs, gen_keys, mw, sep)

    print()


def _print_c_formatted(
    splits_data: list[tuple[str, list[dict[str, Any]]]],
    title: str,
) -> None:
    """Print constraints table with With text / Without text groups.

    *splits_data* is a list of ``(split_label, category_rows)`` tuples.
    """
    groups = ["With text", "Without text"]
    specs: list[tuple[str, int, int]] = [
        ("FB Pos\u2193", 3, 10),
        ("EE Pos\u2193", 3, 10),
        ("EE Rot\u2193", 3, 10),
        ("2D Root\u2193", 3, 11),
        ("Pelvis@95%", 2, 12),
    ]
    gt_keys = [
        "Full-Body Pos (GT, cm)",
        "End-Effector Pos (GT, cm)",
        "End-Effector Rot (deg)",
        "2D Root Pos (GT, cm)",
        "2D Pelvis Pos@95% (GT, cm)",
    ]
    gen_keys = [
        "Full-Body Pos (gen, cm)",
        "End-Effector Pos (gen, cm)",
        "End-Effector Rot (deg)",
        "2D Root Pos (gen, cm)",
        "2D Pelvis Pos@95% (gen, cm)",
    ]

    mw = 16
    gw = sum(s[2] for s in specs)
    sep = " | "
    total_w = mw + len(groups) * gw + (len(groups) - 1) * len(sep)

    print(f"\n{title:^{total_w}}")
    print("=" * total_w)

    parts: list[str] = [" " * mw]
    for i, g in enumerate(groups):
        if i:
            parts.append(sep)
        parts.append(g.center(gw))
    print("".join(parts))

    parts = [f"{'':<{mw}}"]
    for i in range(len(groups)):
        if i:
            parts.append(sep)
        for hdr, _, w in specs:
            parts.append(f"{hdr:>{w}}")
    print("".join(parts))

    parts = ["\u2500" * mw]
    for i in range(len(groups)):
        if i:
            parts.append("\u2500\u253c\u2500")
        parts.append("\u2500" * gw)
    print("".join(parts))

    for si, (split_label, rows) in enumerate(splits_data):
        tag = f"\u2500\u2500 {split_label} "
        print(tag + "\u2500" * (total_w - len(tag)))

        _print_grouped_rows("Ground Truth", rows, specs, gt_keys, mw, sep)
        _print_grouped_rows("Method", rows, specs, gen_keys, mw, sep)

    print()


def _print_formatted_gt_method(
    tables: dict[str, dict[str, list[dict[str, Any]]]],
) -> None:
    """Print combined tables with column groups separated by vertical bars.

    Content and repetition splits are shown as separate row-pairs inside one text-following table
    and one constraints table.
    """
    tf_splits: list[tuple[str, list[dict[str, Any]]]] = []
    c_splits: list[tuple[str, list[dict[str, Any]]]] = []
    for split in SPLITS:
        split_tables = tables.get(split, {})
        tf_rows = split_tables.get("text_following", [])
        c_rows = split_tables.get("constraints", [])
        if tf_rows and len(tf_rows) == 3:
            tf_splits.append((split.capitalize(), tf_rows))
        if c_rows and len(c_rows) == 2:
            c_splits.append((split.capitalize(), c_rows))

    if tf_splits:
        _print_tf_formatted(tf_splits, "Text-Following Evaluation")
    if c_splits:
        _print_c_formatted(c_splits, "Constrained Evaluation")


def _build_summary(root: Path) -> dict[str, Any]:
    testcase_dirs = _discover_testcase_dirs(root)
    if not testcase_dirs:
        raise SystemExit(
            f"No testcase folders found under {root} (expected folders containing meta.json + motion.npz + gt_motion.npz samples)."
        )

    missing_results: list[Path] = []
    for testcase_dir in testcase_dirs:
        result_path = _expected_result_path(testcase_dir)
        if not result_path.is_file():
            missing_results.append(result_path)

    if missing_results:
        missing_text = "\n".join(str(path) for path in missing_results)
        raise SystemExit(f"Missing {len(missing_results)} testcase result JSON files:\n{missing_text}")

    row_acc: dict[tuple[str, str], dict[str, Any]] = {}
    for split in SPLITS:
        for category in ROW_CATEGORIES:
            row_acc[(split, category)] = {
                "num_testcases": 0,
                "num_motions": 0.0,
                "per_motion_mean_weighted_sum": {},
                "per_motion_mean_gt_weighted_sum": {},
                "tmr_weighted_sum": {},
                "tmr_weight": 0.0,
            }

    for testcase_dir in testcase_dirs:
        split, category = _parse_testcase_key(root, testcase_dir)
        result_path = _expected_result_path(testcase_dir)
        num_motions, per_motion_mean_gen, per_motion_mean_gt, tmr = _load_result_row(result_path)

        acc = row_acc[(split, category)]
        acc["num_testcases"] += 1
        acc["num_motions"] += num_motions
        _accumulate_weighted(acc["per_motion_mean_weighted_sum"], per_motion_mean_gen, num_motions)
        if per_motion_mean_gt:
            _accumulate_weighted(acc["per_motion_mean_gt_weighted_sum"], per_motion_mean_gt, num_motions)
        if tmr:
            _accumulate_weighted(acc["tmr_weighted_sum"], tmr, num_motions)
            acc["tmr_weight"] += num_motions

    rows: list[dict[str, Any]] = []
    for split in SPLITS:
        for category in ROW_CATEGORIES:
            acc = row_acc[(split, category)]
            tmr_avg = _to_averages(acc["tmr_weighted_sum"], acc["tmr_weight"]) if acc["tmr_weight"] > 0 else {}
            per_motion_gt_avg = _to_averages(acc["per_motion_mean_gt_weighted_sum"], acc["num_motions"])
            row_dict: dict[str, Any] = {
                "split": split,
                "category": category,
                "num_testcases": acc["num_testcases"],
                "num_motions": int(acc["num_motions"]),
                "per_motion_mean": _to_averages(acc["per_motion_mean_weighted_sum"], acc["num_motions"]),
                "tmr": tmr_avg,
            }
            if per_motion_gt_avg:
                row_dict["per_motion_mean_gt"] = per_motion_gt_avg
            rows.append(row_dict)

        # Combined constraints row for this split.
        withtext = row_acc[(split, "constraints_withtext")]
        notext = row_acc[(split, "constraints_notext")]
        combined_weight = withtext["num_motions"] + notext["num_motions"]

        combined_per_motion = defaultdict(float)
        combined_per_motion_gt = defaultdict(float)
        combined_tmr = defaultdict(float)
        combined_tmr_weight = withtext["tmr_weight"] + notext["tmr_weight"]
        for source in (
            withtext["per_motion_mean_weighted_sum"],
            notext["per_motion_mean_weighted_sum"],
        ):
            for k, v in source.items():
                combined_per_motion[k] += v
        for source in (
            withtext["per_motion_mean_gt_weighted_sum"],
            notext["per_motion_mean_gt_weighted_sum"],
        ):
            for k, v in source.items():
                combined_per_motion_gt[k] += v
        for source in (withtext["tmr_weighted_sum"], notext["tmr_weighted_sum"]):
            for k, v in source.items():
                combined_tmr[k] += v

        combined_tmr_avg = _to_averages(dict(combined_tmr), combined_tmr_weight) if combined_tmr_weight > 0 else {}
        combined_gt_avg = _to_averages(dict(combined_per_motion_gt), combined_weight)
        combined_row: dict[str, Any] = {
            "split": split,
            "category": "constraints",
            "num_testcases": withtext["num_testcases"] + notext["num_testcases"],
            "num_motions": int(combined_weight),
            "per_motion_mean": _to_averages(dict(combined_per_motion), combined_weight),
            "tmr": combined_tmr_avg,
        }
        if combined_gt_avg:
            combined_row["per_motion_mean_gt"] = combined_gt_avg
        rows.append(combined_row)

    tables = _build_tables(row_acc)
    return {
        "folder": str(root),
        "num_testcases": len(testcase_dirs),
        "rows": rows,
        "tables": tables,
    }


def main() -> None:
    parser = argparse.ArgumentParser(
        description=("Validate testcase XXX.json result files and aggregate averages by split/category.")
    )
    parser.add_argument(
        "folder",
        type=Path,
        help="Testsuite root folder (contains content/ and repetition/).",
    )
    parser.add_argument(
        "--output",
        type=Path,
        default=None,
        help="Optional output JSON path. Default: <folder>/summary_rows.json",
    )
    parser.add_argument(
        "--format",
        choices=["terminal", "md"],
        default="terminal",
        dest="table_format",
        help="Table output format: 'terminal' (default) for fixed-width tables, 'md' for markdown.",
    )
    args = parser.parse_args()

    folder = args.folder.resolve()
    if not folder.is_dir():
        raise SystemExit(f"Folder does not exist: {folder}")

    summary = _build_summary(folder)

    out_path = args.output.resolve() if args.output else folder / "summary_rows.json"
    out_path.write_text(json.dumps(summary, indent=2) + "\n", encoding="utf-8")
    print(f"Wrote aggregated summary: {out_path}")
    print(f"Rows: {len(summary['rows'])}, testcases: {summary['num_testcases']}")
    if args.table_format == "md":
        _print_formatted_gt_method_md(summary["tables"])
    else:
        _print_formatted_gt_method(summary["tables"])


if __name__ == "__main__":
    main()

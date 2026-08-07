#!/usr/bin/env python3
"""
Assigns a die activation number to every Unit that does not have one in the
spreadsheet, spreading 1-6 as evenly as possible.

    python3 Tools/assign_activation_numbers.py "/path/to/Card Concepts.xlsx"

Writes two things:

  Tools/generated_activation_numbers.json
      The assignments, keyed by card id. import_cards.py reads this, so the
      numbers stay identical every time the cards are re-imported. Numbers
      typed into the spreadsheet always take priority over this file, so once
      a number is entered in column D the entry here stops having any effect.

  Docs/Activation Numbers.xlsx
      A readable list, in spreadsheet row order, for copying the numbers into
      the master spreadsheet.

Each unit gets a single number. The handful of cards with two numbers were
written with per-number effects ("1: Take 1 damage. 2: Deal 1 damage."), which
is a deliberate design choice rather than something to generate.

Re-running only fills in units that are still missing a number; assignments
already in the JSON are preserved.
"""

import argparse
import json
import os
import random
from collections import Counter

import pandas as pd

from import_cards import COLOR_NAMES, build_cards, parse_activation_numbers, slugify

# Fixed so the assignment is reproducible rather than changing on every run.
RANDOM_SEED = 20260807
DIE_FACES = [1, 2, 3, 4, 5, 6]

TOOLS_DIR = os.path.dirname(os.path.abspath(__file__))
REPO_ROOT = os.path.dirname(TOOLS_DIR)
ASSIGNMENTS_PATH = os.path.join(TOOLS_DIR, "generated_activation_numbers.json")
DOC_PATH = os.path.join(REPO_ROOT, "Docs", "Activation Numbers.xlsx")


def read_master_rows(xlsx_path):
    """Every spreadsheet row, with the 1-based row number shown in Excel."""
    raw = pd.read_excel(xlsx_path, sheet_name="Sheet1", header=None)
    raw = raw.rename(columns={0: "title", 1: "type", 2: "cost", 3: "number", 4: "color", 5: "effect"})

    rows = []
    for position, row in raw.iterrows():
        if row[["title", "type", "cost", "color", "effect"]].isna().any():
            continue
        rows.append(
            {
                "excel_row": position + 1,  # Sheet1 has no header row
                "title": str(row["title"]).strip(),
                "type": str(row["type"]).strip().replace("Passive", "Blessing"),
                "cost": str(row["cost"]).strip().replace(" ", ""),
                "color": COLOR_NAMES.get(str(row["color"]).strip(), str(row["color"]).strip()),
                "effect": str(row["effect"]).strip(),
                "existing": parse_activation_numbers(row["number"]),
            }
        )
    return rows


def balanced_numbers(count, already_used, rng):
    """
    `count` die numbers that even out the spread across every unit in the game.

    Takes the numbers already in the spreadsheet into account, so the totals end
    up level rather than just the newly assigned ones. Each number goes to
    whichever face is currently least represented, ties broken at random.
    """
    totals = Counter(already_used)
    numbers = []

    for _ in range(count):
        fewest = min(totals.get(face, 0) for face in DIE_FACES)
        candidates = [face for face in DIE_FACES if totals.get(face, 0) == fewest]
        face = rng.choice(candidates)
        totals[face] += 1
        numbers.append(face)

    rng.shuffle(numbers)
    return numbers


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("xlsx", help="Path to the card design spreadsheet")
    args = parser.parse_args()

    cards, _ = build_cards(args.xlsx)
    rows = read_master_rows(args.xlsx)

    existing_assignments = {}
    if os.path.exists(ASSIGNMENTS_PATH):
        with open(ASSIGNMENTS_PATH) as f:
            existing_assignments = json.load(f)["assignments"]

    # Units with no number in the spreadsheet and none assigned previously.
    needs_number = [
        card for card in cards
        if card["type"] == "Unit"
        and not card["activationNumbers"]
        and card["id"] not in existing_assignments
    ]
    needs_number.sort(key=lambda c: c["id"])

    already_used = [
        number
        for card in cards
        if card["type"] == "Unit"
        for number in card["activationNumbers"]
    ]
    already_used += list(existing_assignments.values())

    rng = random.Random(RANDOM_SEED)
    for card, number in zip(needs_number, balanced_numbers(len(needs_number), already_used, rng)):
        existing_assignments[card["id"]] = number

    with open(ASSIGNMENTS_PATH, "w") as f:
        json.dump({"assignments": existing_assignments}, f, indent=2, sort_keys=True)
        f.write("\n")

    write_doc(cards, rows, existing_assignments)
    report(cards, existing_assignments, len(needs_number))


def write_doc(cards, rows, assignments):
    cards_by_id = {card["id"]: card for card in cards}
    id_for_row = {}
    seen = Counter()
    for row in rows:
        base = slugify(row["title"])
        seen[base] += 1
        card_id = base if seen[base] == 1 else f"{base}_{seen[base] - 1}"
        id_for_row[row["excel_row"]] = card_id if card_id in cards_by_id else base

    records = []
    for row in rows:
        if row["type"] != "Unit":
            continue

        card_id = id_for_row[row["excel_row"]]
        if row["existing"]:
            source = "already in spreadsheet"
            number = ", ".join(str(n) for n in row["existing"])
        elif card_id in assignments:
            source = "NEW - copy into column D"
            number = str(assignments[card_id])
        else:
            continue

        records.append(
            {
                "Spreadsheet Row": row["excel_row"],
                "Title": row["title"],
                "Activation Number": number,
                "Status": source,
                "Cost": row["cost"],
                "Color": row["color"],
                "Effect": row["effect"],
            }
        )

    os.makedirs(os.path.dirname(DOC_PATH), exist_ok=True)
    frame = pd.DataFrame(records)
    with pd.ExcelWriter(DOC_PATH, engine="openpyxl") as writer:
        frame.to_excel(writer, sheet_name="Activation Numbers", index=False)
        sheet = writer.sheets["Activation Numbers"]
        for column, width in zip("ABCDEFG", (16, 30, 18, 26, 12, 10, 70)):
            sheet.column_dimensions[column].width = width
        sheet.freeze_panes = "A2"

    print(f"Wrote {DOC_PATH}")


def report(cards, assignments, newly_assigned):
    spread = Counter()
    for card in cards:
        if card["type"] != "Unit":
            continue
        numbers = card["activationNumbers"] or ([assignments[card["id"]]] if card["id"] in assignments else [])
        spread.update(numbers)

    print(f"Wrote {ASSIGNMENTS_PATH}")
    print(f"  assigned {newly_assigned} unit(s) this run; {len(assignments)} generated in total")
    print("  final spread across all units (spreadsheet + generated):")
    for face in DIE_FACES:
        print(f"    {face}: {spread[face]} unit(s)")


if __name__ == "__main__":
    main()

#!/usr/bin/env python3
"""
Converts the card design spreadsheet into Assets/Resources/Data/Cards.json.

Re-run this whenever the spreadsheet changes:

    python3 Tools/import_cards.py "/path/to/Card Concepts.xlsx"

Reads the "Sheet1" tab, whose columns are:
    A title | B type | C cost | D die activation number(s) | E color | F effect
Columns G onward are concept-art notes and are ignored. Rows missing any of
title/type/cost/color/effect are skipped as incomplete designs.
"""

import argparse
import datetime
import json
import os
import re
import sys

import pandas as pd

COLOR_NAMES = {"R": "Red", "G": "Green", "B": "Blue", "Y": "Yellow"}
DEFAULT_OUTPUT = "Assets/Resources/Data/Cards.json"

# Die numbers filled in by Tools/assign_activation_numbers.py for units that do
# not have one in the spreadsheet yet. The spreadsheet always takes priority.
ASSIGNMENTS_PATH = os.path.join(os.path.dirname(os.path.abspath(__file__)), "generated_activation_numbers.json")


def parse_activation_numbers(value):
    """
    Die numbers a Unit activates on.

    Excel silently reinterprets entries like "1/2" as dates, so a datetime here
    means two numbers: its month and day (2026-01-02 -> [1, 2]). Plain numbers
    and slash/comma separated text are read as written.
    """
    if value is None or (isinstance(value, float) and pd.isna(value)):
        return []

    if isinstance(value, (datetime.datetime, datetime.date, pd.Timestamp)):
        return [value.month, value.day]

    if isinstance(value, (int, float)):
        return [int(value)]

    numbers = [int(n) for n in re.findall(r"\d", str(value))]
    return numbers


def slugify(title):
    return re.sub(r"[^a-zA-Z0-9]+", "_", str(title).strip()).strip("_")


def build_cards(xlsx_path):
    raw = pd.read_excel(xlsx_path, sheet_name="Sheet1", header=None)
    raw = raw.rename(columns={0: "title", 1: "type", 2: "cost", 3: "number", 4: "color", 5: "effect"})
    raw = raw[["title", "type", "cost", "number", "color", "effect"]]

    complete = raw.dropna(subset=["title", "type", "cost", "color", "effect"]).copy()
    skipped = len(raw) - len(complete)

    for column in ("title", "type", "cost", "color", "effect"):
        complete[column] = complete[column].astype(str).str.strip()

    # "Passive" is retired nomenclature for what is now a Blessing.
    complete["type"] = complete["type"].replace({"Passive": "Blessing"})
    complete["cost"] = complete["cost"].str.replace(" ", "", regex=False)

    cards = []
    seen_ids = {}
    # Identical rows are duplicate physical copies of one card, collapsed into a count.
    for key, group in complete.groupby(
        ["title", "type", "cost", "color", "effect"], sort=False
    ):
        title, card_type, cost, color, effect = key

        card_id = slugify(title)
        if card_id in seen_ids:
            seen_ids[card_id] += 1
            card_id = f"{card_id}_{seen_ids[card_id]}"
        else:
            seen_ids[card_id] = 0

        activation_numbers = []
        for value in group["number"]:
            for number in parse_activation_numbers(value):
                if number not in activation_numbers:
                    activation_numbers.append(number)

        cards.append(
            {
                "id": card_id,
                "title": title,
                "type": card_type,
                "costRaw": cost,
                "color": COLOR_NAMES.get(color, color),
                "effect": effect,
                "count": len(group),
                "activationNumbers": sorted(activation_numbers),
            }
        )

    return cards, skipped


def apply_generated_numbers(cards):
    """
    Fills in die numbers for units the spreadsheet has not assigned yet.
    Returns how many cards were filled in this way.
    """
    if not os.path.exists(ASSIGNMENTS_PATH):
        return 0

    with open(ASSIGNMENTS_PATH) as f:
        assignments = json.load(f)["assignments"]

    filled = 0
    for card in cards:
        if card["type"] != "Unit" or card["activationNumbers"]:
            continue
        if card["id"] in assignments:
            card["activationNumbers"] = [assignments[card["id"]]]
            filled += 1

    return filled


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("xlsx", help="Path to the card design spreadsheet")
    parser.add_argument("-o", "--output", default=DEFAULT_OUTPUT, help=f"Output path (default: {DEFAULT_OUTPUT})")
    args = parser.parse_args()

    cards, skipped = build_cards(args.xlsx)

    units = [c for c in cards if c["type"] == "Unit"]
    from_spreadsheet = sum(1 for c in units if c["activationNumbers"])
    generated = apply_generated_numbers(cards)

    with open(args.output, "w") as f:
        json.dump({"cards": cards}, f, indent=2)
        f.write("\n")

    missing_numbers = [c["title"] for c in units if not c["activationNumbers"]]

    print(f"Wrote {args.output}")
    print(f"  {len(cards)} card definitions ({sum(c['count'] for c in cards)} physical cards)")
    print(f"  skipped {skipped} incomplete row(s)")
    print(f"  die numbers: {from_spreadsheet} from spreadsheet, {generated} generated")
    if missing_numbers:
        print(f"  units still missing numbers: {len(missing_numbers)}", file=sys.stderr)


if __name__ == "__main__":
    main()

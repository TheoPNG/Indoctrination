#!/usr/bin/env python3
"""Render one-card PDFs into Resources using the card database's stable ids."""

import argparse
import hashlib
import json
import re
import shutil
import subprocess
from pathlib import Path


REPO = Path(__file__).resolve().parents[1]
CARD_DATA = REPO / "Assets/Resources/Data/Cards.json"
OUTPUT = REPO / "Assets/Resources/CardArt"

# Printed names can change without invalidating ids already used by effects and
# network views. These are the deliberate filename-to-id exceptions in this set.
ALIASES = {
    "baal": "Baal_The_Manipulator",
    "brainwasher": "Hydro_Plant",
    "doubleagent": "Double_Agent_Japanese_Art",
    "worshipperofthebonegod": "Worshiper_of_the_Bone_God",

    # Green. Spelling on the printed files, against the database's titles.
    "celebtrity": "Celebrity",
    "stayeyed": "Star_Eyed",
    "sufferingfromsucess": "Suffering_from_Success",
    "churchofwalls": "Titanstopper_Church_of_Walls",
}


def normalized(value: str) -> str:
    return re.sub(r"[^a-z0-9]", "", value.lower())


def meta_for(card_id: str) -> str:
    guid = hashlib.md5(f"indoctrination-card-art:{card_id}".encode()).hexdigest()
    return f"""fileFormatVersion: 2
guid: {guid}
TextureImporter:
  internalIDToNameTable: []
  externalObjects: {{}}
  serializedVersion: 13
  mipmaps:
    mipMapMode: 0
    enableMipMap: 0
    sRGBTexture: 1
  isReadable: 0
  streamingMipmaps: 0
  textureFormat: 1
  maxTextureSize: 2048
  textureSettings:
    serializedVersion: 2
    filterMode: 1
    aniso: 1
    mipBias: 0
    wrapU: 1
    wrapV: 1
    wrapW: 1
  nPOTScale: 0
  compressionQuality: 50
  spriteMode: 0
  textureType: 0
  textureShape: 1
  alphaUsage: 1
  alphaIsTransparency: 0
  platformSettings: []
  spriteSheet:
    serializedVersion: 2
    sprites: []
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def main() -> None:
    parser = argparse.ArgumentParser()
    parser.add_argument("source", type=Path, help="Folder containing one PDF per card")
    parser.add_argument("--color", default="Blue", help="Card color to require and import")
    parser.add_argument("--width", type=int, default=700, help="Rendered PNG width")
    parser.add_argument(
        "--allow-missing",
        action="store_true",
        help="Import anyway when some cards of this colour have no PDF yet",
    )
    args = parser.parse_args()

    # pdftoppm gives the better result and is preferred, but it comes from
    # poppler and is not on a stock macOS. sips is, and renders these cards
    # identically at a fixed size, so the import works on a clean machine
    # without asking anyone to install anything first.
    renderer = shutil.which("pdftoppm")
    fallback = shutil.which("sips")

    if renderer is None and fallback is None:
        raise SystemExit("Either pdftoppm (poppler) or sips is required to render card PDFs")

    with CARD_DATA.open(encoding="utf-8") as stream:
        definitions = json.load(stream)["cards"]

    selected = [card for card in definitions if card["color"] == args.color]
    by_printed_name = {normalized(card["title"]): card["id"] for card in selected}
    expected = {card["id"] for card in selected}
    matched = {}

    for source in sorted(args.source.glob("*.pdf")):
        source_name = normalized(source.stem)
        card_id = ALIASES.get(source_name, by_printed_name.get(source_name))
        if card_id is None:
            raise SystemExit(f"No {args.color} card definition matches {source.name}")
        if card_id in matched:
            raise SystemExit(f"Both {matched[card_id].name} and {source.name} match {card_id}")
        matched[card_id] = source

    missing = sorted(expected - matched.keys())
    extra = sorted(matched.keys() - expected)

    # A PDF that matches no card is always an error - it means a card was
    # renamed, or the file belongs to another colour, and importing it would
    # write art under an id nothing reads.
    if extra:
        raise SystemExit(f"PDFs match no card definition: {extra}")

    # A card with no PDF is only an error unless it is expected. Art arrives a
    # colour at a time and sometimes a card is still being drawn, so this is
    # opt-in rather than simply tolerated - the gap has to be stated out loud.
    if missing and not args.allow_missing:
        raise SystemExit(
            f"No PDF for: {missing}\n"
            "Pass --allow-missing to import the rest and leave these without art."
        )

    if missing:
        print(f"WARNING: importing without art for {len(missing)} card(s): {missing}")

    OUTPUT.mkdir(parents=True, exist_ok=True)
    height = round(args.width * 7 / 5)

    for card_id, source in sorted(matched.items()):
        destination = OUTPUT / f"{card_id}.png"

        if renderer is not None:
            command = [
                renderer,
                "-f", "1",
                "-l", "1",
                "-singlefile",
                "-png",
                "-scale-to-x", str(args.width),
                "-scale-to-y", str(height),
                str(source),
                str(destination.with_suffix("")),
            ]
        else:
            command = [
                fallback,
                "-s", "format", "png",
                "--resampleHeightWidth", str(height), str(args.width),
                str(source),
                "--out", str(destination),
            ]

        subprocess.run(command, check=True, stdout=subprocess.DEVNULL)
        destination.with_suffix(".png.meta").write_text(meta_for(card_id), encoding="utf-8")
        print(f"{source.name} -> {destination.relative_to(REPO)}")

    print(f"Imported {len(matched)} {args.color} card faces at {args.width}x{height}.")


if __name__ == "__main__":
    main()

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
    args = parser.parse_args()

    renderer = shutil.which("pdftoppm")
    if renderer is None:
        raise SystemExit("pdftoppm is required to render card PDFs")

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
    if missing or extra:
        raise SystemExit(f"PDF set mismatch; missing={missing}, extra={extra}")

    OUTPUT.mkdir(parents=True, exist_ok=True)
    height = round(args.width * 7 / 5)

    for card_id, source in sorted(matched.items()):
        destination = OUTPUT / f"{card_id}.png"
        subprocess.run(
            [
                renderer,
                "-f", "1",
                "-l", "1",
                "-singlefile",
                "-png",
                "-scale-to-x", str(args.width),
                "-scale-to-y", str(height),
                str(source),
                str(destination.with_suffix("")),
            ],
            check=True,
        )
        destination.with_suffix(".png.meta").write_text(meta_for(card_id), encoding="utf-8")
        print(f"{source.name} -> {destination.relative_to(REPO)}")

    print(f"Imported {len(matched)} {args.color} card faces at {args.width}x{height}.")


if __name__ == "__main__":
    main()

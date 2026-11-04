#!/usr/bin/env python3
"""Use Fedora's OpenCC instead of the helper's unlicensed NuGet dependency."""

import shutil
import sys
from pathlib import Path

root = Path(sys.argv[1])
helper = root / "vendor/Lyricify-Lyrics-Helper/Lyricify.Lyrics.Helper"


def replace_once(path: Path, old: str, new: str) -> None:
    content = path.read_text(encoding="utf-8")
    if content.count(old) != 1:
        raise SystemExit(f"Unexpected Lyricify Lyrics Helper source: {path}")
    path.write_text(content.replace(old, new), encoding="utf-8")


replace_once(
    helper / "Lyricify.Lyrics.Helper.csproj",
    '\t\t<PackageReference Include="CHTCHSConv" Version="1.0.0" />\n',
    "",
)

source = helper / "Helpers/General/ChineseHelper.cs"
for direction, method in (("SimplifiedToTraditional", "ToTraditional"),
                          ("TraditionalToSimplified", "ToSimplified")):
    replace_once(
        source,
        "return Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter.ChineseConverter.Convert(\n"
        "                text,\n"
        "                Microsoft.International.Converters.TraditionalChineseToSimplifiedConverter."
        f"ChineseConversionDirection.{direction});",
        f"return OpenCcConverter.{method}(text);",
    )

shutil.copyfile(root / "packaging/opencc/Converter.cs.in", helper / "Helpers/General/OpenCcConverter.cs")

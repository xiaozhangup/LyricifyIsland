#!/usr/bin/env python3
"""Collect license information for NuGet packages used by the RPM build."""

import shutil
import sys
import xml.etree.ElementTree as ET
from pathlib import Path

packages, output, mit_file = map(Path, sys.argv[1:])
mit_text = "Permission is hereby granted" + mit_file.read_text(encoding="utf-8").split(
    "Permission is hereby granted", 1
)[1]
output.mkdir(parents=True, exist_ok=True)

for nuspec in sorted(packages.glob("*/*/*.nuspec")):
    package = nuspec.parent
    metadata = next(node for node in ET.parse(nuspec).getroot() if node.tag.endswith("}metadata"))
    fields = {node.tag.rsplit("}", 1)[-1]: node for node in metadata}
    license_info = fields.get("license")
    if license_info is None or not license_info.text:
        raise SystemExit(f"NuGet package has no license metadata: {nuspec}")

    prefix = f"{package.parent.name}-{package.name}"
    license_type = license_info.attrib.get("type")
    if license_type == "file":
        shutil.copyfile(package / license_info.text, output / f"{prefix}-LICENSE.txt")
    elif license_type == "expression" and license_info.text == "MIT":
        copyright_info = fields.get("copyright")
        copyright_text = copyright_info.text if copyright_info is not None else ""
        (output / f"{prefix}-LICENSE.txt").write_text(
            f"{prefix}\n{copyright_text or ''}\n\n{mit_text}", encoding="utf-8"
        )
    else:
        raise SystemExit(f"Review the NuGet license for {nuspec}: {license_info.text}")

    for notice in package.glob("THIRD-PARTY-NOTICES*"):
        if notice.is_file():
            shutil.copyfile(notice, output / f"{prefix}-{notice.name}")

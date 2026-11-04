# Third-party notices

## NWayland

The Wayland backend uses [NWayland](https://github.com/AvaloniaUI/NWayland),
version 0.11.0. Its MIT license is included in `licenses/NWayland-MIT.txt`.

## OppoPodsManager device metadata

`Resources/Headphones/DeviceModels.json` and `Resources/Headphones/EqModeNames.json`
are unmodified copies of the corresponding files in
[Zhaoyi-ya/OppoPodsManager](https://github.com/Zhaoyi-ya/OppoPodsManager),
commit `f272e9e95bb20bfb8e317af06be776c26f1eb077` (retrieved 2026-09-26).
They supply model capabilities, protocol indices and Chinese equalizer names.
The upstream files identify OPPO HeyMelody as the origin of the model/EQ facts.

Copyright (C) 2026 Zhaoyi-ya. These files are distributed under the GNU General
Public License, version 3 or later. The upstream copyright notice and complete
license are included in `licenses/OppoPodsManager-GPL-3.0.txt`. Preserve these
notices and provide the corresponding source data when redistributing them.

The Linux connection service, packet handling, capability reader and Avalonia
window in this repository are implemented for LyricifyIsland. Protocol behavior
was checked against the above desktop project and
[Leaf-lsgtky/OppoPods](https://github.com/Leaf-lsgtky/OppoPods), commit
`10a2f04de5911b59b2ed74404602ea46e8741324`. No Android/Xposed integration, upstream
application UI, fonts, illustrations or update service is included.

## HuaweiPods protocol reference

Huawei model capabilities, verified device IDs, SPP framing, battery fields and
control command values were checked against
[Nshpiter/HuaweiPods](https://github.com/Nshpiter/HuaweiPods), commit
`f9c1cac64d59b41bf67b775da33bd038d32bb7fe` (retrieved 2026-09-26).
The reference project is licensed under GPL-3.0. Relevant references include
`HuaweiAncPackets.kt`, `HuaweiAncLevelProfile.kt`, `HuaweiRfcommResponseParser.kt`,
`HuaweiDeviceInfoIdentity.kt`, `HuaweiDeviceInfoRoutePolicy.kt` and the model
controllers under `app/src/main/java/moe/chenxy/huaweipods/pods/`.

The C# Huawei catalog, stream codec and Linux service integration are independently
implemented here from those protocol facts. No HuaweiPods application source,
Android/Xposed integration, images or other assets are bundled. Support describes
the commands implemented in this application, not Huawei hardware validation.

## Lyricify Lyrics Helper

The existing `vendor/Lyricify-Lyrics-Helper` submodule is distributed under its
[Apache-2.0 license](vendor/Lyricify-Lyrics-Helper/LICENSE). The host application's
existing Apache-2.0 license remains in `LICENSE`; the licenses of the bundled
third-party components remain applicable to those components.

For COPR builds, the helper's CHTCHSConv dependency is replaced with Fedora's
[OpenCC](https://github.com/BYVoid/OpenCC), licensed under Apache-2.0. It is
used to convert between simplified and traditional Chinese for lyric matching.

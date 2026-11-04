#!/usr/bin/env bash
set -euo pipefail

project_dir="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
version="${1:-0.1.11}"
topdir="$(mktemp -d)"
trap 'rm -rf -- "$topdir"' EXIT

command -v rpmbuild >/dev/null || { echo 'rpmbuild is required' >&2; exit 1; }
[[ "$version" =~ ^[0-9][0-9A-Za-z._+~]*$ ]] || { echo "invalid RPM version: $version" >&2; exit 1; }

"$project_dir/build.sh"
mkdir -p "$topdir"/{BUILD,BUILDROOT,RPMS,SOURCES,SPECS,SRPMS}
install -m755 "$project_dir/dist/LyricifyIsland" "$topdir/SOURCES/LyricifyIsland"
install -m644 "$project_dir/dist/THIRD_PARTY_NOTICES.md" "$topdir/SOURCES/THIRD_PARTY_NOTICES.md"
install -m644 "$project_dir/dist/licenses/"*.txt "$topdir/SOURCES/"
install -m644 "$project_dir/packaging/lyricify-island.desktop" "$topdir/SOURCES/lyricify-island.desktop"
install -m644 "$project_dir/vendor/Lyricify-Lyrics-Helper/Lyricify.Lyrics.Helper/Resources/icon.png" "$topdir/SOURCES/lyricify-island.png"

cat >"$topdir/SPECS/lyricify-island.spec" <<EOF
%global __strip /bin/true
Name:           lyricify-island
Version:        $version
Release:        1%{?dist}
Summary:        Desktop lyrics island for Spotify and MPRIS
License:        MIT AND Apache-2.0 AND GPL-3.0-or-later
URL:            https://github.com/xiaozhangup/LyricifyIsland
BuildArch:      x86_64
Requires:       libwayland-client, libglvnd-egl

%description
Wayland desktop lyrics island for Spotify and local MPRIS players.

%install
install -Dm755 %{_sourcedir}/LyricifyIsland %{buildroot}%{_bindir}/LyricifyIsland
install -Dm644 %{_sourcedir}/lyricify-island.desktop %{buildroot}%{_datadir}/applications/lyricify-island.desktop
install -Dm644 %{_sourcedir}/lyricify-island.png %{buildroot}%{_datadir}/icons/hicolor/128x128/apps/lyricify-island.png

install -Dm644 %{_sourcedir}/THIRD_PARTY_NOTICES.md %{buildroot}%{_docdir}/%{name}/THIRD_PARTY_NOTICES.md
install -Dm644 %{_sourcedir}/LyricifyIsland-Apache-2.0.txt %{buildroot}%{_licensedir}/%{name}/LyricifyIsland-Apache-2.0.txt
install -Dm644 %{_sourcedir}/Lyricify-Lyrics-Helper-Apache-2.0.txt %{buildroot}%{_licensedir}/%{name}/Lyricify-Lyrics-Helper-Apache-2.0.txt
install -Dm644 %{_sourcedir}/OppoPodsManager-GPL-3.0.txt %{buildroot}%{_licensedir}/%{name}/OppoPodsManager-GPL-3.0.txt
install -Dm644 %{_sourcedir}/NWayland-MIT.txt %{buildroot}%{_licensedir}/%{name}/NWayland-MIT.txt

%files
%doc %{_docdir}/%{name}/THIRD_PARTY_NOTICES.md
%license %{_licensedir}/%{name}/*.txt
%{_bindir}/LyricifyIsland
%{_datadir}/applications/lyricify-island.desktop
%{_datadir}/icons/hicolor/128x128/apps/lyricify-island.png
EOF

rpmbuild -bb --define "_topdir $topdir" "$topdir/SPECS/lyricify-island.spec"
mkdir -p "$project_dir/dist"
find "$topdir/RPMS" -name '*.rpm' -exec install -m644 {} "$project_dir/dist/" \;
printf 'Built %s\n' "$project_dir"/dist/*.rpm

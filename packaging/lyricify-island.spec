%global debug_package %{nil}
%global __provides_exclude_from ^%{_libdir}/%{name}/.*\.so$

Name:           lyricify-island
Version:        0.1.12
Release:        1.0%{?dist}
Summary:        Desktop lyrics island for Spotify and MPRIS
License:        Apache-2.0 AND GPL-3.0-or-later AND MIT AND BSD-3-Clause
URL:            https://github.com/xiaozhangup/LyricifyIsland
Source0:        %{name}-%{version}.tar.gz
Source1:        %{name}-nuget-%{version}.tar
BuildRequires:  dotnet-sdk-10.0
BuildRequires:  gcc
BuildRequires:  opencc-devel
BuildRequires:  python3
Requires:       dotnet-runtime-10.0
Requires:       libwayland-client
Requires:       libglvnd-egl
Requires:       opencc
ExclusiveArch:  x86_64

%description
Wayland desktop lyrics island for Spotify and local MPRIS players.

%prep
%autosetup
tar -xf %{SOURCE1}
python3 packaging/apply-copr-helper.py .

%build
export DOTNET_CLI_HOME="$PWD/.dotnet-home"
export NUGET_PACKAGES="$PWD/.nuget-packages"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1
dotnet restore LyricifyIsland.csproj --runtime fedora.%{fedora}-x64 --source "$PWD/nuget-feed" -p:SelfContained=false -p:NuGetAudit=false
dotnet publish LyricifyIsland.csproj --configuration Release --runtime fedora.%{fedora}-x64 --self-contained false --no-restore -p:DebugType=None --output "$PWD/publish"
gcc $RPM_OPT_FLAGS $RPM_LD_FLAGS -shared -fPIC packaging/opencc/shim.c -o publish/liblyricify-opencc.so -lopencc

%install
install -dm755 %{buildroot}%{_libdir}/%{name} %{buildroot}%{_bindir}
cp -a publish/. %{buildroot}%{_libdir}/%{name}/
ln -s ../%{_lib}/%{name}/LyricifyIsland %{buildroot}%{_bindir}/LyricifyIsland
install -Dm644 packaging/lyricify-island.desktop %{buildroot}%{_datadir}/applications/lyricify-island.desktop
install -Dm644 vendor/Lyricify-Lyrics-Helper/Lyricify.Lyrics.Helper/Resources/icon.png %{buildroot}%{_datadir}/icons/hicolor/128x128/apps/lyricify-island.png
install -Dm644 LICENSE %{buildroot}%{_licensedir}/%{name}/LyricifyIsland-Apache-2.0.txt
install -Dm644 vendor/Lyricify-Lyrics-Helper/LICENSE %{buildroot}%{_licensedir}/%{name}/Lyricify-Lyrics-Helper-Apache-2.0.txt
install -Dm644 licenses/NWayland-MIT.txt %{buildroot}%{_licensedir}/%{name}/NWayland-MIT.txt
install -Dm644 licenses/OppoPodsManager-GPL-3.0.txt %{buildroot}%{_licensedir}/%{name}/OppoPodsManager-GPL-3.0.txt
python3 packaging/write-nuget-licenses.py .nuget-packages "%{buildroot}%{_licensedir}/%{name}/nuget" licenses/NWayland-MIT.txt
install -Dm644 THIRD_PARTY_NOTICES.md %{buildroot}%{_docdir}/%{name}/THIRD_PARTY_NOTICES.md

%files
%license %{_licensedir}/%{name}/
%doc %{_docdir}/%{name}/
%{_bindir}/LyricifyIsland
%{_libdir}/%{name}/
%{_datadir}/applications/lyricify-island.desktop
%{_datadir}/icons/hicolor/128x128/apps/lyricify-island.png

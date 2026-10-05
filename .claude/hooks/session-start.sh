#!/bin/bash
# Make the .NET SDK available to Claude Code on the web sessions.
#
# Adapted from xfmrexpert/Tensile's .claude/hooks/session-start.sh.
#
# The container image has no SDK, and the usual installer is not reachable:
# dotnet-install.sh pulls from builds.dotnet.microsoft.com, which the
# environment's network policy answers with 403. Ubuntu noble-updates/universe
# carries dotnet-sdk-10.0 and archive.ubuntu.com IS reachable, so that is the
# route. apt-get update first -- the image ships a stale index whose dotnet10
# pool paths 404.
#
# Local sessions are left alone; they have their own SDK.
set -euo pipefail

if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
  exit 0
fi

# gmsh: GeomLibrary sets options (e.g. Mesh.MinCurveNodes) that Ubuntu's gmsh
# 4.12 rejects, so use the 4.15 wheel from PyPI (reachable; it installs
# /usr/local/bin/gmsh, ahead of /usr/bin on PATH). The wheel's libgmsh links
# against X11/OpenGL runtime libraries even when run headless.
GMSH_VERSION="4.15"
need_gmsh=true
if command -v gmsh >/dev/null 2>&1 && gmsh --version 2>&1 | grep -q "^${GMSH_VERSION}\."; then
  need_gmsh=false
fi

if ! command -v dotnet >/dev/null 2>&1 || [ "$need_gmsh" = true ]; then
  apt-get update -qq
fi
if ! command -v dotnet >/dev/null 2>&1; then
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq dotnet-sdk-10.0
fi
if [ "$need_gmsh" = true ]; then
  DEBIAN_FRONTEND=noninteractive apt-get install -y -qq \
    libglu1-mesa libxcursor1 libxft2 libxinerama1 libxrender1 libfontconfig1
  pip install -q "gmsh==${GMSH_VERSION}.*" 2>/dev/null \
    || pip install -q --break-system-packages "gmsh==${GMSH_VERSION}.*"
fi

# The project references (..\..\GeomLibrary, ..\..\TfmrLibrary) assume this
# repo is checked out one folder deeper than its sibling repos, while TfmrLib's
# own references assume side-by-side repos. Cloud sessions clone all repos side
# by side, so bind-mount the siblings where electrostat expects them. A symlink
# is not enough: MSBuild then sees each library under two paths and the
# restores collide on the shared obj/ directory.
project_dir="${CLAUDE_PROJECT_DIR:-$(pwd)}"
parent_dir="$(dirname "$project_dir")"
grandparent_dir="$(dirname "$parent_dir")"
for sibling in GeomLibrary TfmrLibrary; do
  src="$parent_dir/$sibling"
  dst="$grandparent_dir/$sibling"
  [ -d "$src" ] || continue
  mountpoint -q "$dst" 2>/dev/null && continue
  mkdir -p "$dst"
  mount --bind "$src" "$dst" || echo "warning: could not bind-mount $src at $dst" >&2
done

# Warm the NuGet cache so the first build in the session is not also the first
# restore. Non-fatal: a restore failure should not stop the session from
# starting.
dotnet restore "$project_dir/electrostat.slnx" || true

dotnet --version
gmsh --version 2>&1 | tail -1

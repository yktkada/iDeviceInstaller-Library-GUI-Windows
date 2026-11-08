#!/usr/bin/env bash
set -euo pipefail

if [[ -n "${GITHUB_WORKSPACE:-}" ]]; then
  if [[ "$GITHUB_WORKSPACE" == [A-Za-z]:* ]]; then
    ROOT="$(cygpath -u "$GITHUB_WORKSPACE")"
  else
    ROOT="$GITHUB_WORKSPACE"
  fi
else
  ROOT="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
fi
mkdir -p "$ROOT/artifacts"
exec > >(tee -a "$ROOT/artifacts/build-toolkit.log") 2>&1
set -x
find "$ROOT/third_party" -type f \( -name 'autogen.sh' -o -name 'git-version-gen' \) -exec chmod +x {} +
chmod +x "$ROOT/scripts/build-toolkit.sh" 2>/dev/null || true

PREFIX="${TOOLKIT_PREFIX:-/mingw64}"
STAGE="$ROOT/artifacts/toolkit"
JOBS="${JOBS:-$(nproc)}"

mkdir -p "$PREFIX" "$STAGE"
export MSYSTEM=MINGW64
export MSYSTEM_CHOST=x86_64-w64-mingw32
export MSYSTEM_PREFIX=/mingw64
export PATH="$PREFIX/bin:/mingw64/bin:$PATH"
export ACLOCAL_PATH="/mingw64/share/aclocal:/usr/share/aclocal:${ACLOCAL_PATH:-}"
export PKG_CONFIG_PATH="$PREFIX/lib/pkgconfig:/mingw64/lib/pkgconfig:${PKG_CONFIG_PATH:-}"
export CPPFLAGS="-I$PREFIX/include ${CPPFLAGS:-}"
export LDFLAGS="-L$PREFIX/lib ${LDFLAGS:-}"
export CFLAGS="-O2 ${CFLAGS:-}"
export CXXFLAGS="-O2 ${CXXFLAGS:-}"

build_project() {
  local name="$1"; shift
  echo "=== building $name ==="
  cd "$ROOT/third_party/$name"
  ACLOCAL_PATH="/mingw64/share/aclocal:/usr/share/aclocal:${ACLOCAL_PATH:-}" sh ./autogen.sh --prefix="$PREFIX" "$@"
  make -j"$JOBS"
  make install
  cd "$ROOT"
}

build_project libplist --without-cython
build_project libimobiledevice-glue
build_project libusbmuxd
build_project libtatsu
build_project libimobiledevice --without-cython
build_project ideviceinstaller

# Copy direct runtime dependencies (DLLs) from the MSYS2 prefix and the
# local build prefix into the toolkit staging directory.
copy_deps() {
  local queue=("$@")
  local seen=" "
  while ((${#queue[@]})); do
    local file="${queue[0]}"
    queue=("${queue[@]:1}")
    [[ -f "$file" ]] || continue
    while IFS= read -r line; do
      local dll
      dll=$(awk '{print $3}' <<<"$line")
      [[ -f "$dll" ]] || continue
      local base
      base=$(basename "$dll")
      [[ "$seen" == *" $base "* ]] && continue
      # Never ship Windows system DLLs; only MSYS2/runtime libraries.
      local unix
      unix=$(cygpath -u "$dll" 2>/dev/null || printf '%s' "$dll")
      case "$unix" in
        /mingw64/*|"$PREFIX"/*) ;;
        *) continue ;;
      esac
      seen="$seen$base "
      cp -f "$dll" "$STAGE/"
      queue+=("$dll")
    done < <(ldd "$file" 2>/dev/null | grep '=>')
  done
}

cp -f "$PREFIX/bin/ideviceinstaller.exe" "$STAGE/"
cp -f "$PREFIX/bin/idevice_id.exe" "$STAGE/"
cp -f "$PREFIX/bin/ideviceinfo.exe" "$STAGE/"
cp -f "$PREFIX/bin/idevicediagnostics.exe" "$STAGE/"

copy_deps \
  "$STAGE/ideviceinstaller.exe" \
  "$STAGE/idevice_id.exe" \
  "$STAGE/ideviceinfo.exe" \
  "$STAGE/idevicediagnostics.exe"

# Defensive copies for packages commonly needed by libzip/OpenSSL builds.
for pat in \
  libzip*.dll libssl*.dll libcrypto*.dll zlib1.dll \
  libbz2*.dll liblzma*.dll libzstd*.dll libiconv*.dll libintl*.dll \
  libgcc_s_seh-1.dll libwinpthread-1.dll libstdc++-6.dll; do
  for f in /mingw64/bin/$pat; do
    [[ -f "$f" ]] && cp -f "$f" "$STAGE/"
  done
done

echo "=== toolkit files ==="
ls -1 "$STAGE" | sed -n '1,120p'
echo "=== toolkit ready: $STAGE ==="

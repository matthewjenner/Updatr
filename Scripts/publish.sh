#!/usr/bin/env bash
#
# Build the portable release folders and zips locally, exactly as CI does.
# Usage:  ./Scripts/publish.sh [rid...]        (default: win-x64 win-arm64)
#
# Output: dist/Updatr-<version>-<rid>/  and  dist/Updatr-<version>-<rid>.zip
# Each folder is what goes on the thumb drive: Updatr.exe, updatr-cli.exe, and
# Microsoft.Management.Deployment.winmd (must stay beside the exes - see
# Directory.Build.targets).
#
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR/.."

rids=("$@")
if [[ ${#rids[@]} -eq 0 ]]; then
    rids=(win-x64 win-arm64)
fi

version=$(grep -oE '<VersionPrefix>[^<]+</VersionPrefix>' Directory.Build.props \
    | sed -E 's|</?VersionPrefix>||g' \
    | tr -d '[:space:]')

if [[ -z "$version" ]]; then
    echo "Could not read VersionPrefix from Directory.Build.props" >&2
    exit 1
fi

# The files that make up a release. Anything else publish emits (package .pdb files) stays out.
release_files=(Updatr.exe updatr-cli.exe Microsoft.Management.Deployment.winmd)

rm -rf dist
mkdir -p dist

for rid in "${rids[@]}"; do
    staging="publish/$rid"
    rm -rf "$staging"

    printf '\n==> Publishing %s\n' "$rid"
    dotnet publish Src/Updatr.App --configuration Release --runtime "$rid" --output "$staging" --nologo
    dotnet publish Src/Updatr.Cli --configuration Release --runtime "$rid" --output "$staging" --nologo

    name="Updatr-$version-$rid"
    mkdir -p "dist/$name"
    for f in "${release_files[@]}"; do
        if [[ ! -f "$staging/$f" ]]; then
            echo "Expected $f in $staging but it is missing" >&2
            exit 1
        fi
        cp "$staging/$f" "dist/$name/"
    done

    # Windows' own bsdtar writes zip archives (-a picks the format from the extension). Git Bash's
    # `tar` is GNU tar, which cannot, so call the system one by path. Present on every Windows 10+
    # machine and on GitHub's Windows runners.
    win_tar="$(cygpath -u "$SYSTEMROOT")/System32/tar.exe"
    (cd dist && "$win_tar" -a -c -f "$name.zip" "$name")
done

# Standard "<hash>  <file>" lines; self-update parses this file to verify downloads.
(cd dist && for f in *.zip; do
    printf '%s  %s\n' "$(sha256sum < "$f" | cut -d' ' -f1)" "$f"
done > SHA256SUMS.txt)

printf '\n==> Done\n'
ls -la dist
cat dist/SHA256SUMS.txt

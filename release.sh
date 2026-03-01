#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
cd "$SCRIPT_DIR"

MOD_NAME=$(jq -r '.name' modpack.json)
VERSION=$(jq -r '.version' modpack.json)
RELEASE_DIR="release/${MOD_NAME}"

rm -rf "$RELEASE_DIR"
mkdir -p "$RELEASE_DIR"

cp modpack.json "$RELEASE_DIR/"
cp README.md "$RELEASE_DIR/"
[[ -f CHANGELOG.md ]] && cp CHANGELOG.md "$RELEASE_DIR/"
cp -r src "$RELEASE_DIR/"
cp -r docs "$RELEASE_DIR/"

ZIP_FILE="release/${MOD_NAME}-modpack-v${VERSION}.zip"
rm -f "$ZIP_FILE"
(cd release && zip -r "../${ZIP_FILE}" "${MOD_NAME}/")

echo "${MOD_NAME} v${VERSION} → ${ZIP_FILE}"

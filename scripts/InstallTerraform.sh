#!/usr/bin/env bash
set -euo pipefail

version=1.14.3
checksum=178b2a602251bb68b94732aceca2cc1023d87597cb83dba92cab31b6689edb4d
archive="${AGENT_TEMPDIRECTORY}/terraform_${version}_linux_amd64.zip"
bin="${AGENT_TEMPDIRECTORY}/terraform-bin"
mkdir -p "$bin"
curl --fail --silent --show-error --location --retry 3 \
  "https://releases.hashicorp.com/terraform/${version}/terraform_${version}_linux_amd64.zip" -o "$archive"
printf '%s  %s\n' "$checksum" "$archive" | sha256sum --check
unzip -o -q "$archive" terraform -d "$bin"
echo "##vso[task.prependpath]$bin"

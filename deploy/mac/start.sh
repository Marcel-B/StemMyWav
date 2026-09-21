#!/bin/zsh
set -e

project=/Users/marcel/repos/StemMyWav
key_file="$HOME/.config/stemmywav/mac-api-key"
if [[ ! -s "$key_file" ]]; then
  echo "Mac API key missing: $key_file" >&2
  exit 1
fi

export MacApi__Key="$(<"$key_file")"
export Separator__Executable="$project/.venv/bin/mlx-audio-separator"
export Separator__ModelDirectory="$project/.models"
export ASPNETCORE_URLS=http://127.0.0.1:5081
export PATH=/opt/homebrew/bin:/usr/local/bin:/usr/bin:/bin
exec /usr/local/share/dotnet/dotnet "$project/deploy/mac/publish/StemMyWav.Api.dll"

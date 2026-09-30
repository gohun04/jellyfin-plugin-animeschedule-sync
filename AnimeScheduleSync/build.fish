#!/usr/bin/env fish
set -l here (dirname (status --current-filename))
cd $here

if not command -q dotnet
    echo "dotnet was not found."
    echo "Install the .NET 10 SDK first, then run this script again."
    exit 1
end

set -l sdk10 (dotnet --list-sdks | string match -r '^10\.')
if test -z "$sdk10"
    echo ".NET 10 SDK was not found."
    echo "Install dotnet-sdk-10.0 and aspnet-targeting-pack-10.0, then retry."
    echo
    dotnet --list-sdks
    exit 1
end

echo "Building AnimeSchedule Sync for Jellyfin 12..."
dotnet restore
or exit $status

dotnet publish -c Release -o dist/publish
or exit $status

set -l package_dir "dist/AnimeSchedule Sync_0.5.2.0"
rm -rf "$package_dir"
mkdir -p "$package_dir"

cp dist/publish/Jellyfin.Plugin.AnimeScheduleSync.dll "$package_dir/"
cp meta.json "$package_dir/"

echo
echo "Build complete:"
echo "$here/$package_dir"
echo
echo "Copy that whole folder into Jellyfin's plugins directory and restart Jellyfin."
echo "For your LinuxServer container that is normally:"
echo "  host:      /mnt/user/appdata/jellyfin/data/plugins/"
echo "  container: /config/data/plugins/"

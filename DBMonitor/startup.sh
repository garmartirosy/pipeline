#!/bin/bash
set -e

# Ensure Python is available for the Pipelines feature.
# The App Service .NET runtime image does not ship with Python; installing here
# works because the platform runs the startup command as root before the app
# process starts (which runs as `app` and cannot apt-get itself).
if ! command -v python3 >/dev/null 2>&1; then
    apt-get update -qq
    apt-get install -y --no-install-recommends python3 python3-pip
fi

exec dotnet /home/site/wwwroot/DBMonitor.dll

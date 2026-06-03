#!/usr/bin/env bash
set -e
cd "$(dirname "$0")"
dotnet restore avalonia/PixelWizard.AvaloniaClient/PixelWizard.AvaloniaClient.csproj
dotnet run --project avalonia/PixelWizard.AvaloniaClient/PixelWizard.AvaloniaClient.csproj

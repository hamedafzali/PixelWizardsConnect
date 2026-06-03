@echo off
cd /d "%~dp0"
dotnet restore avalonia\PixelWizard.AvaloniaClient\PixelWizard.AvaloniaClient.csproj
dotnet run --project avalonia\PixelWizard.AvaloniaClient\PixelWizard.AvaloniaClient.csproj

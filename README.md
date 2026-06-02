# PixelWizard Connect

A Windows-based remote desktop automation prototype with network support for remote connections and a Docker-friendly Go router server.

For the product direction, phased implementation plan, competitor notes, and AI strategy, see [docs/ROADMAP.md](docs/ROADMAP.md).

## Platform Support

### Router Server (Go)
- ✅ **Cross-platform** - Runs on Windows, Linux, macOS
- ✅ **Docker-ready** - Containerized deployment for easy server deployment
- ✅ Production-ready for cloud servers
- See `router-server/README.md` for deployment details

### Client Application (C# WPF)
- ⚠️ **Windows-only** - WPF is Windows-specific
- Requires Windows 10 or later
- .NET 8.0 required

### Cross-Platform Client Options
To make the client cross-platform, consider:
1. **Avalonia UI** - Cross-platform XAML-based framework (Windows, Linux, macOS)
2. **.NET MAUI** - Microsoft's cross-platform framework (Windows, iOS, Android, macOS)
3. **Web Client** - Browser-based client using WebRTC for screen sharing

**Recommendation:** Avalonia UI would be the best choice as it uses XAML (similar to WPF) and maintains most of the current code structure.

## Features

- **Remote Server Mode**: Host your desktop for remote connections
- **Remote Client Mode**: Connect to a remote desktop
- **Direct Connect**: Connect directly via IP address
- **Router Server Mode**: Use a signaling server for connection code-based connections
- **Screen Change Detection**: Efficient screen streaming by sending only changed regions
- **Mouse and Keyboard Input**: Send mouse clicks and keyboard inputs to remote desktop
- **System Tray Integration**: Minimize to tray when connected
- **Modern UI**: Professional, compact interface with clean design

## Requirements

- .NET 8.0 SDK or later
- Windows operating system
- Visual Studio 2022 or compatible IDE (optional)

## Building the Application

```bash
cd "c:\repos\Microsoft UI Automation"
dotnet build
```

## Running the Application

```bash
dotnet run
```

Or build and run from Visual Studio.

## How to Use

### 1. Select a Screen
- Use the "Select Screen" dropdown to choose which monitor to display
- Click "Refresh Screens" if you've connected/disconnected monitors

### 2. View Screen Capture
- The selected screen will be displayed in the main area
- Check "Auto-refresh screen (2s)" for live updates
- Manual refresh occurs when you perform automation actions

### 3. Find and Click Elements by Name
- Enter the UI element's name in the "Element Name" text box
- Click "Find and Click" to:
  - Search for the element across all applications
  - Click the element if found
  - Refresh the screen to show the result

### 4. Find Elements by Automation ID
- Enter the Automation ID in the "Automation ID" text box
- Click "Find by ID" to:
  - Locate the element
  - Display its name and class name
  - Show its bounding rectangle coordinates

## Finding Element Names and Automation IDs

To find element names and automation IDs, you can use:


## Technical Details


### Screen Capture
Uses `System.Drawing` to capture screen contents:
- `Graphics.CopyFromScreen()` for capture
- Converts to BitmapImage for WPF display

### Project Structure
- `App.xaml/cs` - Application entry point
- `MainWindow.xaml` - UI layout
- `MainWindow.xaml.cs` - Core logic and automation
- `ScreenAutomationApp.csproj` - Project configuration

## Limitations

- Requires Windows OS
- Some applications may not fully support UI Automation
- Admin privileges may be needed for certain applications
- UWP apps have limited automation support

## Troubleshooting

### "UI Automation not initialized"
- Ensure UIAutomationClient COM library is properly referenced
- Run as administrator if needed

### "Element not found"
- Verify the element name/ID using Inspect.exe
- Some elements may have dynamic names
- Try using Automation ID instead of Name

### Screen capture not working
- Ensure the app has display access permissions
- Check if the selected screen is still connected

## Next Steps

Potential enhancements:
- Add element highlighting on screen
- Support for more automation patterns (text input, selection)
- Record and replay automation sequences
- Support for keyboard automation
- Element tree viewer

# Waypoint Queue

## Installation

### Requirements
- [Unity Mod Manager](https://www.nexusmods.com/site/mods/21)

### Steps:
1. Download the latest release
2. Open UMM Installer and make sure you have Railroader selected
3. Click the Mods tab
4. Drag and drop the `WaypointQueue_versionNumber.zip` file into UMM
5. Start up the game


### Incompatibilities
- Incompatible with Refuel Waypoint, however Waypoint Queue has built-in support for refueling locomotives.

## Overview
Waypoint Queue significantly expands the Auto Engineer (AE) Waypoint mode feature from vanilla Railroader. For the latest information on mod features, check the [description on the Nexus Mods page](https://www.nexusmods.com/railroader/mods/1029).

All the keybinds are reconfigurable using the UMM Mod Settings menu which you can access by pressing Ctrl+F10.


## Local Development

### Requirements
- Visual Studio (in the installer, make sure you have SDKs for .NET Framework 4.8, C# support, and NuGet Package Manager)

To start developing locally, follow these steps:
1. Clone the repo locally
2. Copy `Paths.user.example` and save it as `Paths.user`.
3. Open this `Paths.user` file and update the path to the game directory containing `Railroader.exe`
4. Open the Solution

### During Development
- Make sure you're using the *Debug* configuration. Every time you build your project, the files will be copied to your Mods folder and you can immediately start the game to test it.
- If you are interested in contributing to Waypoint Queue, please read the [Contributing guide](https://github.com/PrinceOfPluto/WaypointQueue/blob/main/CODE_OF_CONDUCT.md).
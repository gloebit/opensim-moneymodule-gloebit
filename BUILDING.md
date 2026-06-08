# Building the Gloebit Money Module

## Quick Start (OpenSim 0.9.3+ / .NET 8)

Pre-built DLLs are available from [GitHub Releases](https://github.com/gloebit/opensim-moneymodule-gloebit/releases). If you need to build from source:

### Prerequisites
- .NET 8 SDK ([download](https://dotnet.microsoft.com/download/dotnet/8.0))
- OpenSim 0.9.3 source code
- On Linux: `apt-get install -y libgdiplus libc6-dev`
- On macOS: `brew install mono-libgdiplus`

### Steps

```bash
# 1. Clone OpenSim
git clone --branch 0.9.3.0 https://github.com/opensim/opensim.git
cd opensim

# 2. Clone Gloebit module into addon-modules
git clone https://github.com/gloebit/opensim-moneymodule-gloebit.git
cp -r opensim-moneymodule-gloebit/addon-modules/Gloebit addon-modules/

# 3. Copy System.Drawing.Common.dll for your platform
# Linux/macOS:
cp bin/System.Drawing.Common.dll.linux bin/System.Drawing.Common.dll
# Windows:
# copy bin\System.Drawing.Common.dll.win bin\System.Drawing.Common.dll

# 4. Generate project files
# Linux/macOS:
./runprebuild.sh
# Windows:
# runprebuild.bat

# 5. Build
dotnet build --configuration Release OpenSim.sln

# 6. DLL is at bin/Gloebit.dll
```

## Building for older OpenSim (pre-0.9.3 / Mono / .NET 4.x)

Use the [`pre-net8-last-stable`](https://github.com/gloebit/opensim-moneymodule-gloebit/tree/pre-net8-last-stable) tag:

```bash
git clone --branch pre-net8-last-stable https://github.com/gloebit/opensim-moneymodule-gloebit.git
```

Then follow the same steps but using Mono instead of .NET 8:

```bash
# Prerequisites: mono and mono-devel >= 5.12
./runprebuild.sh
msbuild  # or nant
```

## CI / GitHub Actions

The repository includes a GitHub Actions workflow (`.github/workflows/build-dll.yml`) that automatically builds DLLs when a `build-*` tag is pushed.

### Triggering a new build

```bash
git tag -a build-89 -m "Build 89: description"
git push origin build-89
```

This builds against all configured OpenSim versions (currently 0.9.3.0 and 0.9.3.1Dev) on Linux, Windows, and macOS, and creates a GitHub Release with all DLLs attached.

### Manual trigger

You can also trigger a build from the GitHub Actions tab → "Build Gloebit DLL" → "Run workflow". You can optionally specify different OpenSim versions to build against.

### Adding new OpenSim versions

Edit `.github/workflows/build-dll.yml` and update the default version list. The versions refer to branch or tag names in the `opensim/opensim` repository.

### Platform notes

- **Linux/macOS**: Uses bash shell, needs `libgdiplus` installed
- **Windows**: Uses cmd shell for prebuild (bash interprets `/target` as a path)
- **System.Drawing.Common.dll**: Not tracked in git (gitignored as `*.dll`). The `.dll.linux` and `.dll.win` variants are tracked. CI copies the appropriate one before building.
- **.NET 6 runtime**: Needed alongside .NET 8 SDK because OpenSim's `prebuild.dll` targets .NET 6

## DLL naming convention

Release DLLs are named: `Gloebit--{OPENSIM_VERSION}--{PLATFORM}--{DATE}--{COMMIT}.dll`

Example: `Gloebit--0.9.3.0--linux--2026-06-07--de5bc63.dll`

Grid operators should rename to `Gloebit.dll` when copying to their OpenSim `bin/` directory.

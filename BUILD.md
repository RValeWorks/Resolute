# Building Resolute

This repository contains every C# compiler input for version 0.7.26, unchanged.
`SOURCE_MANIFEST.json` records their hashes and the matching DLL. Artwork is
distributed in the runtime release; editable artwork is not required to compile
the integration code.

Install a .NET SDK and reference your own Nuclear Option 0.34.1 installation with
BepInEx 5. The release compiler was SDK 10.0.300.

```powershell
./tools/build_plugin.ps1 -GamePath "<your Nuclear Option folder>"
```

The output is `build/plugin/Resolute.dll`.
The build script does not install files or download dependencies. Game binaries,
BepInEx and Harmony remain separate dependencies and are not included here.

Compiler or game-reference changes can produce different output. A locally rebuilt
base DLL must keep the release's matching `Assets/` directory beside it.

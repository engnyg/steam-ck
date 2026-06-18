# Engine binaries

`EngineService` deploys the OpenSteamTool native engine from this folder into the
Steam root directory. Place the three built DLLs here:

- `OpenSteamTool.dll`
- `dwmapi.dll`
- `xinput1_4.dll`

Build them from the `external/OpenSteamTool` submodule:

```powershell
# from repo root
./build-engine.ps1
```

The script runs `external/OpenSteamTool/build.bat` (CMake + MSVC) and copies the
Release DLLs into this folder. They are git-ignored — build locally or let CI
produce them.

## Local build requirements

OpenSteamTool is a Win32 DLL, so building it needs the **Windows SDK**
(`rc.exe` / `mt.exe`) in addition to the MSVC C++ compiler. Install the
**"Desktop development with C++"** workload via the Visual Studio Installer —
the MSVC compiler alone is not enough (CMake will fail at the resource-compile
step with "No CMAKE_C_COMPILER could be found" or a failing `rc` pass).

If you don't want to install the SDK locally, the GitHub Actions `engine` job
(`.github/workflows/ci.yml`) builds the DLLs on `windows-latest` and uploads
them as the `engine-dlls` artifact.


# VIGIL iOS port (tools only)

Runs the retail Windows build of VIGIL (Unity 6000.0.26f1) on a jailbroken / TrollStore iPhone inside an
IL2CPP iOS player built with the same Unity version. **This repository contains no game files and nothing
derived from them** - you need your own copy of the game. Patched assemblies, SDK libraries and the built
app live in a separate private repository.

## Pipeline

1. `tools/patcher` - Mono.Cecil patcher for the game assemblies (Windows P/Invokes, data paths, FMOD /
   Steam Audio retargeted to the statically linked iOS libraries, blink input from ARKit).
2. CI (`.github/workflows/build-shell.yml`) - builds the iOS player on macOS and fakesigns it for TrollStore.
3. `tools/convert_data.py` - converts the game data on Windows:
   * DX11 shaders and compute shaders -> Metal (HLSLcc, `convert_shader.py`, `convert_compute.py`);
   * BCn textures -> ASTC, BC6H / half lightmaps -> ASTC HDR, 4096 px maps -> 2048 (`transcode_tex.py`,
     needs `tools/bc6hdec` built with CMake);
   * the player files are re-tagged for iOS.
   `tools/verify_data.py` checks the result.
4. `tools/package_ipa.py` - produces `Vigil.ipa` and `VigilData.zip` (add `--serve` to download both over Wi-Fi).

## Installing on the iPhone

1. Install `Vigil.ipa` with TrollStore.
2. Put `VigilData.zip` into Files > On My iPhone > VIGIL and tap it to unpack (or unpack it there with Filza).
3. Start VIGIL. The log is at On My iPhone > VIGIL > `Vigil-iOS.log`; tap the FPS counter to see it in game.

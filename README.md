# Geothermal Tweaks (Solar Expanse mod)

A [BepInEx 5](https://github.com/BepInEx/BepInEx) mod for **Solar Expanse** that lets you build
geothermal power on geologically active worlds, not just Io. In the base game the geothermal
plant needs more "Geological Activity" than any body except Io has, so it's greyed out on Earth,
Mars and everywhere else. With this mod each body gets a realistic geothermal output based on how
much internal heat it really has.

## Features

**Geothermal where it makes sense**

| Body | Output | Why |
|---|---|---|
| Io | 100% | volcanic |
| Earth | 65% | plate tectonics |
| Europa, Enceladus | 45% | tidal heating |
| Venus | 40% | volcanic history, weak heat flow |
| Mars | 30% | weak residual heat |
| Luna, Mercury, Ganymede, Titan, Triton, Ceres | 20% | little internal heat; some may have cryovolcanism |
| Pluto | 15% | possible cryovolcanism |
| Everything else (asteroids, geologically dead moons) | can't build | |

- Output is the plant's rated power times the body's share: a geothermal plant on Mars makes 30%
  of what it makes on Io.
- Where geothermal can't be built you get the game's own "Cannot build: Geological Activity too
  low" message.
- Applies to both geothermal plants, and to AI companies as well as you.

**Unchanged:** everything else about geothermal plants (cost, crew, build time, research), and
every other power source. The game's own "internal flux" values aren't touched, so temperatures
and habitability are exactly as before.

## Installing

1. Install **BepInEx 5** (64-bit Windows, e.g. `BepInEx_win_x64_5.4.23.x.zip` from the
   [BepInEx releases](https://github.com/BepInEx/BepInEx/releases)): extract it into the Solar
   Expanse game folder, so `BepInEx` sits next to `Solar Expanse.exe`, then start the game once
   and close it.
2. Download `GeothermalTweaks-<version>.zip` from this repository's
   [Releases](../../releases) and extract it into the same game folder. The DLL ends up at
   `BepInEx\plugins\GeothermalTweaks\GeothermalTweaks.dll`.
3. Start the game.

To uninstall, delete the `BepInEx\plugins\GeothermalTweaks` folder. Saves are unaffected: the mod
stores nothing in them. Geothermal plants you've already built stay where they are and go back
to the base game's rules.

## Settings

`BepInEx\config\laserbees.solarexpanse.geothermaltweaks.cfg` is created on the first start:

| Setting | Default | |
|---|---|---|
| `BodyOutput` | `IO=1, EARTH=0.65, …` | Output share per body as `NAME=share`, comma separated. Names as the game shows them in capitals (`MARS`, `LUNA`). Unlisted or `0` means geothermal can't be built there. |
| `FacilityIds` | `build_power_geothermal,build_power_geothermal_big` | Which buildings count as geothermal plants. |
| `LogBodies` | `true` | Writes the list of bodies where geothermal can be built to `BepInEx\LogOutput.log` once per session. |

## Building from source

Requires the .NET SDK (any recent version, e.g. 8 or 9) and the game with BepInEx installed, since
the project references the game's own DLLs.

```
dotnet build -c Release
```

The build copies `GeothermalTweaks.dll` into `<game>\BepInEx\plugins\GeothermalTweaks\`. If the
game isn't at `F:\SteamLibrary\steamapps\common\Solar Expanse`, edit `GamePath` in
`GeothermalTweaks.csproj` or pass it in:

```
dotnet build -c Release -p:GamePath="D:\Path\To\Solar Expanse"
```

Close the game before building; Windows locks the DLL while the game is running.

### Project layout

- `Plugin.cs`: everything: settings, the build check, the output share and the body report

See [HOW_IT_WORKS.md](HOW_IT_WORKS.md) for how it works and which parts of the game it hooks.

## License

[MIT](LICENSE)

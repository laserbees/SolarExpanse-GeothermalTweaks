# How it works

Technical notes on what this mod changes in Solar Expanse, which parts of the game it hooks, and
why each change is done the way it is. The [README](README.md) covers what the mod does from a
player's point of view; this file is for anyone reading or changing the code.

All game code is patched with [Harmony](https://harmony.pardeike.net/) postfixes and field
accessors. Nothing in the game's assets, scenes or saves is modified; the one data change
(a build requirement) is made in memory at load.

- [Why geothermal only works on Io](#why-geothermal-only-works-on-io)
- [Why a table and not the game's flux](#why-a-table-and-not-the-games-flux)
- [Build check](#build-check)
- [Output](#output)
- [Body report](#body-report)
- [Robustness](#robustness)
- [Game code touched](#game-code-touched)

---

## Why geothermal only works on Io

Buildings carry a `CanBuildParameter`. Besides the `canBuildBy` mode (which tools like Teddit
show), it holds a `terraformParameterCanBuild` list of `TerraformParameterOneCanBuild` entries:
a habitability parameter or planet characteristic with a `min` and `max`. `CanBuildParameter.CanBuild`
checks every entry before anything else, and `GetText` turns failures into "Cannot build: X too
low/high".

The standard geothermal plant (`build_power_geothermal`) has one entry: `InternalFlux` (shown as
"Geological Activity") between 0.1 and 1. The large plant (`build_power_geothermal_big`) has none.

The game's internal flux values, from the mod's first diagnostic run:

| Flux | Bodies |
|---|---|
| 0.5 | Jupiter, Saturn, Uranus |
| 0.4 | Io |
| 0.25 | all ~80 asteroids |
| 0.08 | 28 bodies, including Earth, Mars, Venus, Ceres and the icy moons |
| 0.01 | Luna |
| 0.001 | Phobos, Deimos |

So only Io (and the gas giants, which have no surface) passes 0.1.

## Why a table and not the game's flux

The obvious fix, lowering the minimum and scaling output by flux, doesn't work well with these
values: 0.08 is a shared default, so Earth and Callisto would be equal, and every asteroid
(0.25) would beat Earth. Internal flux also feeds the temperature model and the volcanism penalty
in the habitability score, so changing it per body would have side effects.

Instead the mod uses its own table of output shares (setting `BodyOutput`), based on real heat
flow: Io's volcanism, Earth's plate tectonics, tidal heating on Europa and Enceladus, weak
residual heat on Mars, Venus and the Moon. The game's flux values are left alone.

## Build check

**`Requirements.Apply`**, run on every scene load: for each geothermal plant (`FacilityIds`),
find its `InternalFlux` entry in `terraformParameterCanBuild`, or add one if it has none (creating
the list if needed). The entries are remembered in a `HashSet`. Building data
(`FacilityBaseDescriptor`) is a ScriptableObject shared for the whole session, so each plant is
handled once.

**Postfix on `TerraformParameterOneCanBuild.Check`**: when the entry is one of ours, the result
becomes `Ok` if the body's table share is above 0 and `TooLow` otherwise. Everything that asks the
building whether it can be built (the build menu, its tooltip, and the AI's
`ProductionItemTryToBuild`, `BuildingPriorityGate` and `EnergySupplyPriorityGate`) goes through
this check, so the player and AI companies follow the same rules, and the vanilla "Geological
Activity too low" message still appears.

Identifying our entries by object rather than by building matters: `Check` gets the building as an
optional argument that some callers leave out.

## Output

`EnergyProductionFacility` multiplies a plant's power by `Facility.HabitabilityEfficiencyMultiplier`,
the field that habitability efficiency curves normally set. A **postfix on
`Facility.UpdateHabitabilityEfficiencyMultiplier`** sets that field to the body's table share
for geothermal plants, through a Harmony `FieldRef`.

Bodies are looked up by `ObjectInfo.TrueObjectName` (the capitalised name Teddit and other mods
use, e.g. `MARS`), then by `idTranslation`.

## Body report

A **postfix on the private `ObjectInfo.OnEachMonthUpdateHabitabilityAndVisualization`** (the
monthly callback) runs once per session and writes the bodies where geothermal can be built to the
BepInEx log. Off with `LogBodies = false`.

## Robustness

- Each patch class is applied on its own inside a try/catch, so if a game update breaks one, the
  others still load and the failure is logged.
- `Requirements.Apply` and the report catch their own errors and log them.
- Nothing is stored in saves, so the mod can be added or removed at any time.

## Game code touched

| Game code | Patch | Purpose |
|---|---|---|
| `CanBuildParameter.TerraformParameterOneCanBuild.Check` | postfix | answer the geothermal build check from the table |
| `Facility.UpdateHabitabilityEfficiencyMultiplier` | postfix | set geothermal output to the table share |
| `ObjectInfo.OnEachMonthUpdateHabitabilityAndVisualization` (private) | postfix | one-off body report |
| `FacilityBaseDescriptor.CanBuildParameter.terraformParameterCanBuild` | data, in memory | find or add the `InternalFlux` entry |

# SloppyMods - Human Resources

An HR manager for a RimWorld colony (RimWorld 1.7 on the Waylith engine). It adds an **HR** button to the
bottom bar, a **Title** column to the Work tab, and optional badges on colonist portraits.

This is an early build. It compiles and has been used in one heavily modded colony, but it has not had
systematic testing.

## What it does

| Page | What it shows |
| --- | --- |
| Time use | How each colonist spent the last hours or days: each kind of work, sleep, eating, recreation, idling, and how much of it was walking. Click a category to hide it and recalculate. |
| Job advisor | Suggested work priorities per colonist, scored from skill, passions, traits, genes, health and (with Vanilla Skills Expanded) expertise. Apply per colonist or for everyone, undo the last apply, and optionally let it review assignments once a day. |
| Workload | Which kinds of work are keeping up and which are not, tracked over two days. Tells apart work that needs more workers from work nobody is assigned to. Click a row to step the camera through the waiting work and see why it is not being done. |
| Job titles | Named templates of work priorities that you create and assign. A colonist can hold up to three titles, merged in order. The advisor leaves title holders alone and only reports when a title is a poor fit. Titles can be saved to a library shared between all games. |

Mod options (Options, Mod settings, SloppyMods - Human Resources) let you leave workbenches or bills out of
the workload analysis, for standing bills that only have work now and then.

## Works with

- **[FSF] Complex Jobs**: its split work types (smith, fabricate, and so on) are tracked and advised separately.
- **Vanilla Skills Expanded**: expertise and its passions are counted in the fit scores.

Neither is required.

## Installing

This repository is the mod, so it can be cloned straight into the mods folder (RimSort does this):

```sh
git clone https://github.com/kanid99/SloppyMod-Rimworld-HumanResources.git "RimWorld/Mods/HumanResources"
```

Requires Harmony. The compiled assembly is committed in `Assemblies/`.

## Building

```sh
cd Source/HumanResources
dotnet build -c Release -p:GameRefs=<folder with Assembly-CSharp.dll, UnityStubs.dll and 0Harmony.dll>
```

The three reference assemblies come from a RimWorld 1.7 (Waylith) install and are not in this repository.
The build writes `Assemblies/SloppyHR.dll`.

## Layout

```
About/                  mod metadata
Assemblies/             the compiled mod (committed)
Defs/                   the HR button and the Work tab column
Changelog/              per-build change notes in Steam BBCode
Source/HumanResources/  C# source
```

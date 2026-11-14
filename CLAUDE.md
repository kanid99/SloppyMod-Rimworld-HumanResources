# Working on this repo

Same rules as the other SloppyMods repositories:

- **The default branch is the release.** RimSort clones `main` into the Mods folder, so every commit
  pushed to `main` is what the game loads next. Each commit must load and play as-is.
- **Snapshot every build on its own branch.** Build numbers are `0.9.<commit count>`. Push each commit to
  `main` and to `build/0.9.N`:

      git push origin HEAD:main HEAD:refs/heads/build/0.9.N

  Never move or delete a build branch.
- **Change notes with every update** in `Changelog/0.9.N.bbcode` (Steam BBCode, written for players),
  in the same commit as the change.
- **Commit the compiled assembly** `Assemblies/SloppyHR.dll`, rebuilt in the same commit as any C# change.
- **Stamp the build number before every commit:** `<modVersion>` in `About/About.xml` and the `Build x.y`
  line at the top of its description.
- Keep `packageId` (`sloppymod.humanresources`) unchanged.

## This mod

- RimWorld 1.7 on the Waylith engine only. The project targets `net10.0` and builds against the game's
  `Assembly-CSharp.dll`, `UnityStubs.dll` and `0Harmony.dll` (pass their folder as `-p:GameRefs=`).
- Namespace `SloppyHR`. Def names are prefixed `SloppyHR_`.
- `PrototypeMigration` (in `HumanResourcesMod.cs`) carries data over from the prototype that lived in a
  private compat mod under the namespace `Harmony3Compat`. Keep it until no saves from that prototype remain.
- Nothing here has automated tests. Say plainly when a change compiles but has not been run in game.

## Steam Workshop page

- `Workshop/description.bbcode` is the Workshop page description, kept here for safe keeping. Steam's limit is
  8,000 characters. Its images are loaded from this repository's `main` branch, so the repository must be
  public for them to show.
- The banners, tiles, `About/Preview.png` and `About/ModIcon.png` are drawn by
  `python3 Source/Art/make_workshop.py <folder with the Archivo .ttf files>`. Change the text there and re-run;
  don't edit the PNGs by hand. Update the description and tiles when a feature they describe changes.

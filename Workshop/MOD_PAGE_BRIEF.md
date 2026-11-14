# SloppyMods - Human Resources: mod page brief

Handoff document for building the Steam Workshop page. Everything below is taken from the mod's
source at build 0.9.15. Numbers and labels are the ones the player sees in game.

One caveat to respect on the page: the mod compiles and is in use in one heavily modded colony, but
it has had no systematic testing. Do not claim it is stable, tested or compatible with anything not
listed here.

---

## 1. Identity

| Field | Value |
|---|---|
| Mod name | **SloppyMods - Human Resources** |
| Short name in game | HR (the bottom-bar button says "HR") |
| Author | SloppyMod |
| Package ID | `sloppymod.humanresources` |
| RimWorld version | 1.7 only |
| Required mods | **Harmony** |
| DLC required | None |
| Optional integration | **[FSF] Complex Jobs**, **Vanilla Skills Expanded** (and passions built on it, such as Alpha Skills) |
| Source | https://github.com/kanid99/SloppyMod-Rimworld-HumanResources |
| Licence | MIT |

## 2. The pitch

RimWorld's Work tab tells you what you asked your colonists to do. It doesn't tell you what they
actually did, who should be doing what, or which jobs are quietly never getting done.

Human Resources is an HR manager for the colony. It watches how every colonist spends their time,
works out who is best at what from more than raw skill level, spots the work that is falling behind
and says why, and lets you run the whole colony from **job titles** you design yourself: "Doctor",
"Crafter", "Hauler", stacked on a colonist in the order you choose.

It adds **no buildings, items or research**. It is one button on the bottom bar, one column on the
Work tab, and optional badges on the colonist bar.

## 3. What it adds to the interface

| Where | What |
|---|---|
| Bottom bar | An **HR** button opening a window with four pages |
| Work tab | A **Title** column next to each colonist's name |
| Colonist bar | Optional two-letter badge on each portrait showing what they are doing |
| Options > Mod settings | A page for leaving workbenches and bills out of the workload analysis |

Whoever is selected in the game is highlighted and scrolled into view on every HR page, and picking
a colonist inside HR selects them in the game.

## 4. The four HR pages

### 4.1 Time use
How each colonist really spent their time.

- One stacked bar per colonist, split by category: each kind of work, plus Sleep / rest, Eating,
  Recreation, Idle / wandering, Drafted, Downed / medical rest, Mental break, Group activity.
- Three ranges: **Last 6 hours**, **Last 24 hours**, **Last 3 days**. Three days of history are kept.
- **Walking** can be split out, to see how much of a job is travel.
- Click a category in the legend to **hide it and recalculate** the shares from what is left.
  Right-click ranks colonists by that category. "Show all" brings everything back.
- Each colonist's best skill is shown next to their name.
- Sampling: what every colonist is doing is recorded once every 60 ticks (once a second at normal speed).

### 4.2 Job advisor
Suggested work priorities for every colonist, with the reasons.

- Each colonist gets a **role** (up to three, such as "Cook, Butcher"), or is marked Generalist or Support.
- **Fit score** per kind of work, built from:
  - skill level,
  - passion, read from the learning rate it actually gives, so passions added by other mods count,
  - health and work speed,
  - traits and genes that change the stats that work uses,
  - expertise, with Vanilla Skills Expanded.
- **Worker quality score** (0 to 100): how valuable a colonist's skilled work is to this colony right
  now. Unskilled jobs (hauling, cleaning and so on) go to the lowest scores first.
- Work that lists a skill but doesn't use it is treated as unskilled: Complex Jobs' Nurse (feeding and
  carrying patients), and any bench whose recipes use no skill (refining chemfuel, for example).
- Benches are kept for the best: a bench job with a strong priority 1 worker is kept clear of weaker
  colonists unless they have a passion or expertise in it.
- The number of colonists at priority 1 on a job is matched to how many hours of that work the colony
  actually did in the last day.
- Neglected unskilled jobs are rotated daily among low-scoring colonists, one specific job each.
- Controls: **Apply** per colonist, **Apply all**, **Undo last apply**, and cells showing changes as `3>1`.
- Colonists who hold a job title are never changed, only reported on.

### 4.3 Workload
Which work is keeping up and which is not, tracked over two days.

| Status | Meaning |
|---|---|
| Clear | Nothing waiting |
| Keeping up | Work arrives and gets done |
| Slow | There has been a backlog for a day, but it is being worked |
| Falling behind | Backlog for a day and at least 25% bigger than two days ago |
| Needs more workers | Backlog for two days with steady work on it and still not shrinking |
| Not being worked | Backlog for a day and almost nobody touched it: the assignments are wrong |
| Open bills | Bills waiting at benches |
| Stuck bills | Bills waiting for a day with nobody working them (often missing materials) |

- Columns: work, status, waiting now, two days ago, how long it has not been clear, labour in the last
  24 hours, who is on it, a 48-hour bar chart, and one suggestion.
- Each suggestion changes **one colonist**, names them, and has its own Apply button.
- **Show me where**: click a kind of work to step the camera through each thing that is waiting, with
  what looks wrong with it: forbidden, no path from the colony, in fog, nowhere to store it, or
  already claimed by a colonist on the way.
- Repair work only counts damage inside the home area that can actually be repaired.
- "Leave out..." on a bench row excludes a bill or a kind of workbench that only has work now and then.

### 4.4 Job titles
The headline feature. A job title is a named template of work priorities.

**Titles view**
- Create a title, name it, and set its priorities on a row laid out exactly like the Work tab: same
  order, same boxes, same clicks (left-click raises the priority, right-click lowers it).
- **Copy colonist** starts a title from someone's current priorities. **Copy title** duplicates one.
- Editing a title updates everyone who holds it.
- Each holder gets a one-line review: how much of their work time went to the title's main jobs,
  whether they are idle, and whether someone else would suit it much better.
- **Best suited** lists the three best candidates for the title.

**Colonists view**
- Pick a colonist to see their titles as an ordered list. **Drag** rows to reorder.
- A colonist can hold **up to five titles**.
- **Merging rule:** every title is used exactly as written. The only conflict is two titles setting
  the same kind of work, and then the title higher in the list wins.
- A row of work boxes shows the priorities the game actually uses. A coloured bar under each box
  shows which title it comes from.
- Boxes are shaded by skill and carry the passion icon, as on the Work tab, with the skill level
  written underneath. Work the colonist can't do has no box.
- A panel lists the colonist's **expertise, traits and genes that affect work**, with the effect and
  which jobs it helps. Ones that make them worse are in orange.
- The list of colonists can be filtered by title held, or to those with none.

**Default title**
- One title can be marked as the default. Every colonist holds it automatically as their lowest
  title, new colonists get it when they join, and it does not use one of the five slots.
- It can be switched off for single colonists.

**Hand edits are kept**
- Change a title holder's box by hand on the Work tab and it is remembered on top of their titles,
  marked with a white frame in HR and a `*` in the Title column. One button clears them.

**Library**
- Save a title to the library and load it into any other game. Work types that don't exist in that
  game (because a mod is missing) are skipped and listed.

## 5. The Title column on the Work tab

- Shows each colonist's titles, such as "Doctor + Crafter". Orange when the review has a concern.
- Click a colonist's cell to add, replace, reorder or remove titles.
- Click the column header to **assign a title to all**, **add a title to all**, **remove titles from
  all**, or sort by title.
- All title menus are alphabetical.

## 6. Options

**On the HR window, saved with each game**

| Option | Default | What it does |
|---|---|---|
| Badges on portraits | On | Two-letter coloured badge on each colonist portrait showing their current activity |
| Daily review | Off | Re-checks work assignments once a day and sends a letter saying what changed |
| Rest unused work | Off | Switches off a kind of work nobody has needed for 3 days, and switches it back on within an hour of work appearing. Medical, firefighting, rescue, childcare and warden work are never rested |

**Options > Mod settings > SloppyMods - Human Resources, shared by all games**

| Option | What it does |
|---|---|
| Workbenches (ticked = counted) | Untick a kind of workbench to leave all its bills out of the workload analysis |
| Bills (ticked = counted) | Untick single bills, for standing bills that only have work now and then |
| Job title library | Stored here too, so titles are available in every game |

Nothing changes a colonist's work priorities until the player presses Apply or assigns a title.

## 7. Mod support

| Mod | What HR does with it |
|---|---|
| [FSF] Complex Jobs | Its split work types (Smith, Fabricate, Machining, Surgeon, Nurse and the rest) are tracked, advised and titled separately. Nurse is treated as unskilled care work and kept off colonists with a Doctor or Surgeon role |
| Vanilla Skills Expanded | Expertise counts toward fit and is listed per colonist. Its passions are read by the learning rate they give |
| Mods adding passions (Alpha Skills and similar) | Counted and shown, with the mod's own icon where it can be found |
| Mods adding work types | Appear automatically on every page and in titles |

Neither Complex Jobs nor Vanilla Skills Expanded is required.

## 8. FAQ material

- **Versions:** RimWorld 1.7 only.
- **Add to an existing save:** yes. Time tracking starts when it is added, so Time use and Workload
  need a day or two of game time before they say much.
- **Remove mid-save:** colonists keep whatever priorities they have. Expected to log a one-time error
  about the missing HR data. Not tested.
- **Does it change priorities on its own:** no, unless Daily review or Rest unused work is turned on.
- **Performance:** one sample per colonist per second, one count of waiting work per hour. The
  advisor calculates only while its page is open, or once a day with Daily review on.
- **Two titles both use priority 1:** both stay at 1 and the game picks left to right, as on the
  Work tab. Set one to 2 in its title to force an order.
- **"Stuck bills" on a bill that is fine:** it has nothing to do right now. Use "Leave out...".

## 9. Art direction (for page visuals)

The mod has no in-world art. Its look is the game's own interface: dark panels, the Work tab's
priority boxes (green 1, yellow 2, tan 3, grey 4) and slanted column labels.

Existing page assets, in `Workshop/` in the repository, follow the other SloppyMods pages:
- 1000 x 170 banners: dark diagonal-striped background, small cyan letterspaced kicker, large
  off-white title in Archivo Black, cyan bar along the bottom.
- 1000-wide tile images: dark tiles with a cyan left edge, bold heading, grey body text.
- Palette: background `#1E1D1B`, tile `#262523`, text `#F0EDE4`, body `#C8C5BE`, accent cyan `#3EC8DC`.
- Icon: a staff ID badge on a clip, cream card with a cyan header (`About/ModIcon.png`).
- Preview: `About/Preview.png`, 640 x 360.
- They are drawn by `Source/Art/make_workshop.py`.

**Missing and needed:** real in-game screenshots. None exist yet. The four most useful would be the
Colonists view (titles list, work boxes, traits panel), the Workload page with mixed statuses, the
Time use bars, and the Work tab showing the Title column.

## 10. Suggested page emphasis

1. **Job titles** are the hook: build "Doctor" once, stack it with "Crafter" and a default baseline,
   and reuse it in every colony. Lead with the Colonists view.
2. **It tells you why work isn't getting done**: "needs more workers" versus "nobody is assigned",
   and the click-through to the stuck item.
3. **Advice from more than skill level**: passions (including modded ones), traits, genes and
   expertise, with unskilled work kept off your specialists.
4. **Time use**: the simplest feature to show in one screenshot, and the easiest to understand.
5. **Nothing happens until you say so**: all automation is off by default.
6. **No new content to learn**: one button and one column.

## 11. Things not to claim

- Not tested beyond one colony; no compatibility claims beyond section 7.
- Expertise percentages shown in the Colonists view are an estimate of how Vanilla Skills Expanded
  scales them.
- The game versions list is 1.7 only. It does not run on 1.6 or earlier.

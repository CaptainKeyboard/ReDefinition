# Writing these pages

**For:** anyone who writes or changes a page under `docs/`.
**You need:** the repository, and the page you are working on.
**You get:** the rules every page here follows, and the check to run before you commit
one.

The repository's own `README.md` is the mod's front page and follows none of this. The
check script leaves it and `docs/project/reviews.md`, which is a log, alone.

## The four kinds of page

Each page serves one need, after [Diátaxis](https://diataxis.fr/):

| Kind | Answers | Example here |
|---|---|---|
| Getting started | "Show me how this works at all." | [modders/getting-started.md](../modders/getting-started.md) |
| How to | "I have this job to do." | [player/installing.md](../player/installing.md) |
| Reference | "What exactly does this key do?" | [modders/registration-reference.md](../modders/registration-reference.md) |
| Explanation | "Why is it built this way?" | [settings-store.md](settings-store.md) |

Do not mix them on one page. A reference table with a paragraph of reasoning in it
serves neither reader: put the reasoning in an explanation page and link to it.

Four pages are none of the four kinds, and that is on purpose: the index
[README.md](../README.md), the [glossary](../glossary.md),
[credits-and-licences.md](../credits-and-licences.md) and
[project/status.md](../project/status.md).

## How a page starts

Every page opens with a heading and three lines, in this order:

```
**For:** who reads this page.
**You need:** what has to be there first.
**You get:** what the reader can do or look up afterwards.
```

Then the first section. No preamble, and no history of how the thing came to be.

## Who the reader is

A player page is for someone who plays KSP, not for someone who knows the code.

A page under `modders/` is for an author who **already has a mod**. It says how to
connect that mod to ReDefinition. It never explains how to write a mod, how C# works,
or what a config file is. If an example shows the author's own side, say so: "For
example, your mod holds a switch like this", followed by "That is your mod as it
already is". Only code the author adds **for ReDefinition** is written as an
instruction.

A page under `development/` is for someone changing ReDefinition itself, who has the
repository open.

## How to write the sentences

* Address the reader as **you**. Write about ReDefinition as **it**. Never write "we"
  or "I", in headings either.
* One idea per sentence. A sentence over 25 words is too long; split it. Nothing
  measures this, so read the page once for it.
* **No dash asides.** ` -- ` splits a sentence into two halves the reader has to hold
  in their head. Make the aside its own sentence, a list item or a table row. Inside a
  table row a dash is allowed, and the check script ignores those lines.
* Active voice: say who does it. "The store writes the value", not "the value is
  written".
* Put the condition first, and open it with **If** or **When**: "If the mod is not
  installed, the row is not shown." Not "Where", which reads as a place.
* Write English, not German in English words. These four are the ones this repository
  keeps producing, and the check script warns about them:

  | Instead of | Write |
  |---|---|
  | the row stands in the tab | the row is in the tab |
  | its values count over the defaults | its values take precedence over the defaults |
  | the value is put right | the value is corrected |
  | the frame before's position | the previous frame's position |
* Use the definite article for the things this repository has: **the** example mod,
  **the** settings window, **the** proxy. Use "a" for one of many: a registration, a
  bundled mod, a setting.
* Use one word per thing, the one in [the glossary](../glossary.md), every time. Add
  the word there when it is missing.

## How a page looks

* Headings name a task or a thing, in sentence case: "Add a setting to the window", not
  "Settings", not "Available", not "Why we did it this way" and not a question.
* Numbered lists for steps in order, bullets for everything else.
* A table where every row has the same shape. Give each column a heading that names
  what is in it. Keep a cell to one sentence or a short phrase; where a cell needs
  two sentences, the page probably wants a section instead.
* Code, file names, config keys and values in `code font`. Paths from the repository
  root: `src/Settings/BundledStore.cs`.
* Interface words in *italics*: *Apply*, *Reset*, the *Controls* tab.
* Wrap prose at 95 characters. Table rows may be longer.
* A fact is explained on one page. Other pages name it and link to that page rather
  than explaining it again.

## Where a claim comes from

The pages that carry research mark every claim with its source, and the marks mean the
same everywhere:

| Mark | Means |
|---|---|
| **[src]** | read from source: a clone of the mod's repository, or a decompile of the installed DLL |
| **[doc]** | a vendor's or a project's own statement |
| **[meas]** | measured, with the setup named |
| **[open]** | not verified yet |

Use them in the explanation pages under `development/`, and in
[reference/graphics-mod-compatibility.md](../reference/graphics-mod-compatibility.md).
The other reference pages name a source per row instead, which suits a table better. A
player page carries no marks.

## Before you commit a page

1. Read the first three lines. Do they say who it is for, what is needed and what the
   reader gets?
2. Search the page for ` -- ` outside a table. Every hit is a sentence waiting to be
   split.
3. Is every section one of the four kinds the page claims to be?
4. Does every heading name a task or a thing?
5. Do the terms match the glossary?
6. Run the check:

   ```
   powershell -NoProfile -ExecutionPolicy Bypass -File tools\check_docs.ps1
   ```

   It fails on a link that leads nowhere, a path under `src/`, `tools/`, `tests/`,
   `unity/`, `licenses/` or `third_party/` that is not there, and a code block that is
   not closed. It reports, without failing, a page without the three opening lines, a
   dash aside in prose, and a line over 95 characters. With `-Strict` those three fail
   the run as well.

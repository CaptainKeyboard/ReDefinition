# Writing code comments

**For:** anyone who writes or changes code in this repository.
**You need:** the repository, and the code you are changing.
**You get:** what a comment has to say, how long it may be, and which checks run on it.

The developer documentation of ReDefinition is the comments in the code. There is no
second copy to keep in step. [The wiki](https://github.com/CaptainKeyboard/ReDefinition/wiki)
is generated from the comments, and `docs/` holds the pages for players and mod authors.

## What gets a comment

Every declaration has one: types, methods, properties, fields, constants, enum members,
functions, macros, shader passes, config nodes and ini keys. Every file has an opening
comment, or the comment of the one type in it.

| Element | The comment says |
|---|---|
| File, script, shader | What the file is for, who calls it, and where the work starts. |
| Type | What one instance is, who creates it, and what it must never be used for. |
| Method, function | What the caller gets, what it changes, and what happens on failure. |
| Property, field, constant | What the value means, its unit and range, and who sets it. |
| Enum member | When the value is chosen, and what follows from it. |
| Config node, ini key | What the setting does to the player, and its default. |

A run of fields, constants or enum members without a blank line between them can share
one comment, written above the first. A C++ function declared in a header and defined in a
source file is documented once, in either place.

## What a comment says

* **Say what the element is or does.** The first sentence is complete and stands on its
  own. A reader who stops after it knows enough to use the element.
* **Say why, where the code cannot.** A value that was measured names the measurement and
  its date. A workaround names the thing it works around.
* **Name the neighbours.** Write `Type.Member` for a member, and the path from the
  repository root for a file, such as `src/Proxy/PacketRing.cs`. DocLock checks both, so
  a comment cannot point at something that is gone.
* **Use the present tense.** Describe what the code does now.
* **Use the words of the [glossary](../glossary.md)**, the same word for the same thing.

## What a comment leaves out

* A repeat of the name or the code. "Gets the width" on `GetWidth` says nothing.
* The history. "Previously", "formerly" and "was changed" belong in the commit message.
* A to-do. Write an issue, not a comment.
* An apology, an opinion of the code or a name of a person.

## How long it is

One idea per sentence. The limits below are what the lint enforces.

| Limit | Value |
|---|---|
| A sentence | 40 words |
| A member's comment | 110 words |
| A type's comment | 220 words |
| A file's comment | 320 words |
| Dashes and semicolons in one comment | 5 |

A comment over the limit holds more than one thing. Split it, and put each fact on the
element it is about. A class comment says what the class is and points to the members
that carry the details.

Write a dash aside as its own sentence. Write a list of items as a list, one clause each,
not as a sentence of nouns joined by commas.

## The checks

`doclock` is built from `tools/DocLock`:

```
dotnet build tools/DocLock -c Release
dotnet tools/DocLock/bin/Release/net10.0/DocLock.dll check
```

| Command | What it does |
|---|---|
| `check` | Fails on an undocumented element, a dirty one, a broken reference or a new lint finding. |
| `report` | Writes the coverage per area to `.doclock/coverage.md`. |
| `list` | Names the elements that need attention, with file and line. |
| `lint` | Counts the comments the rules above find fault with; `lint <rule>` lists them. |
| `confirm` | Declares the comment of a dirty element still true. |
| `update` | Records the code and comment fingerprints in `.doclock/lock.tsv`. |
| `wiki` | Writes the wiki pages from the comments. |

An element is **dirty** when its code changed and its comment did not. The test
`DeveloperDocumentationTests` runs `check`, so a change that leaves a comment behind fails
the build of the unit tests.

When an element is dirty, read the new code and then do one of two things. If the comment
is still true, run `confirm` for that element. If it is not, correct the comment. Then run
`update`.

The lint cannot tell whether a comment is true or helps. Reviews do, with a sample of the
changed comments and a random sample of the rest. Their result is in
`.doclock/review.md`.

## Before you commit

1. Read each comment you wrote as a reader who has never seen the file.
2. Run `check`. Fix what it names.
3. Run `lint`. The finding count must not rise.

# Writing code comments

**For:** anyone who writes or changes code in this repository, human or AI.
**You need:** the code you are changing.
**You get:** when a comment is worth writing, what it says, how long it is, and which checks run.

A comment earns its place when it tells the reader something the code does not. The code says
what happens. A comment says what the code cannot: what the thing is for, why it is done this way,
what must not change, and what a measurement showed. No element needs a comment because it exists.

## When to write one

* **Why.** A decision that the code does not explain: a workaround, an order that matters, a
  value that was measured. Name the measurement and its date.
* **What it is for.** A type, a file or a setting whose purpose is not clear from its name.
* **What must not change.** A lifetime, a thread, a layout that the proxy shares, a hook order.
* **What the caller gets.** The meaning, unit and range of a value, or what happens on failure.

## When not to

* To repeat the name or the code. "Gets the width" on `GetWidth` says nothing.
* To tell the history. "Previously" and "was changed" belong in the commit message.
* To leave a to-do or an apology. Write an issue.

A comment that is wrong is worse than none: the reader believes it. When you change code, read
the comment above it and correct it, or delete it.

## How to write it

* The first sentence is complete and says what the thing is or does.
* One idea per sentence, 40 words at most. Write an aside as its own sentence.
* Use the present tense and the words of the [glossary](../glossary.md).
* Name a neighbour as `Type.Member`, and a file by its path from the repository root, such as
  `src/Proxy/PacketRing.cs`. The check below verifies both exist.

Size limits that the lint enforces for a comment you write or change: a member 110 words, a type
220, a file 320, and no more than 5 dashes and semicolons. A comment over a limit holds more than
one thing. Put each fact on the element it is about.

## The checks

```
dotnet build tools/DocLock -c Release
dotnet tools/DocLock/bin/Release/net10.0/DocLock.dll check
```

`check` fails when a comment names a file, a switch or a member that does not exist, and when a new
or changed comment breaks the limits above. It does not fail on a missing comment. The unit test
`DeveloperDocumentationTests` runs it. `.doclock/lint-baseline.tsv` lists the old comments the
lint accepts; it shrinks as they are rewritten.

Other commands: `report` writes the share of elements with a comment to `.doclock/coverage.md`,
`lint [rule]` lists what the lint finds, and `wiki <folder>` writes the wiki.

## Whether a comment is true

No tool can tell. A review does: a reader compares a sample of comments with their code and
corrects what is stale. `.doclock/review.md` records the first one. Draw a new sample with
`sample <count> <seed> <commit>` after a large change.

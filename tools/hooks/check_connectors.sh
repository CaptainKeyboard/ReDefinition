#!/bin/sh
# What a commit says about the connectors, checked against what it changes.
#
# check_connectors.sh MESSAGE_FILE CHANGED_FILES_FILE
#
# Every commit ends with one line, a git trailer:
#   Connectors: unchanged
#   Connectors: changed: <what changed, and what that does to a player's file>
# "Connectors" are everything a player or another mod sees of ReDefinition and a release must not change unnoticed: the settings
# and their standards, the bundled mods' registrations, the graphics profiles, the proxy's ini, the catalogue of other mods' code.
# src/Migration/surface.txt lists them (tools/Surface); the commit that changes a connector changes that file.
#
# unchanged  is refused where surface.txt changes in the commit.
# changed    where surface.txt changes needs src/Migration/Migrations.cs in the commit too, which says what a player's file does,
#            or the words "no step needed" and the reason.
#
# Used by .githooks/commit-msg (the staged files) and by the check on GitHub (each pushed commit's files).

message_file="$1"
files_file="$2"
surface="src/Migration/surface.txt"
steps="src/Migration/Migrations.cs"

first_line=$(head -n 1 "$message_file")
case "$first_line" in
    "Merge "*|"Revert \""*|"fixup! "*|"squash! "*) exit 0 ;;
esac

line=$(grep -i -E '^Connectors:[[:space:]]*' "$message_file" | tail -n 1)
if [ -z "$line" ]; then
    echo "The commit message needs a last line saying what changed in the connectors:" >&2
    echo "  Connectors: unchanged" >&2
    echo "  Connectors: changed: <what changed, and what that does to a player's file>" >&2
    exit 1
fi

value=$(echo "$line" | sed -E 's/^[Cc]onnectors:[[:space:]]*//')
surface_changed=0
steps_changed=0
grep -q -x "$surface" "$files_file" && surface_changed=1
grep -q -x "$steps" "$files_file" && steps_changed=1

case "$value" in
    unchanged|Unchanged)
        if [ "$surface_changed" = 1 ]; then
            echo "$surface changes in this commit, so the connectors did change." >&2
            echo "Say what: 'Connectors: changed: <what, and what it does to a player's file>'." >&2
            exit 1
        fi
        ;;
    changed:*|Changed:*)
        text=$(echo "$value" | sed -E 's/^[Cc]hanged:[[:space:]]*//')
        if [ "${#text}" -lt 8 ]; then
            echo "'Connectors: changed:' needs words after it: what changed, and what that does to a player's file." >&2
            exit 1
        fi
        if [ "$surface_changed" = 1 ] && [ "$steps_changed" = 0 ]; then
            case "$text" in
                *"no step needed"*) ;;
                *)
                    echo "$surface changes without a step in $steps." >&2
                    echo "Add the step that says what a player's file does, or write 'no step needed: <reason>' in the line." >&2
                    exit 1
                    ;;
            esac
        fi
        ;;
    *)
        echo "'Connectors:' must be 'unchanged' or 'changed: <what>', not '$value'." >&2
        exit 1
        ;;
esac
exit 0

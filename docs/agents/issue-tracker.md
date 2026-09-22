# Issue tracker

ORelay uses local Markdown for specs and tickets. Keep these files in Git so other agents and worktrees can read them. GitHub hosts the repository, but publishing a ticket means writing a local Markdown file unless James asks to use GitHub Issues.

## Conventions

- Keep each feature or delivery effort under `.scratch/<feature-slug>/`.
- Write its spec at `.scratch/<feature-slug>/spec.md`.
- Write one file per implementation ticket at `.scratch/<feature-slug>/issues/<NN>-<slug>.md`, numbered from `01`. Do not combine tickets into one file.
- Record the triage label in a `Status:` line near the top. Use the labels in `docs/agents/triage-labels.md`.
- Append comments and conversation history under `## Comments` at the bottom of the file.
- Treat these files as project records, not disposable temporary files.

## Publishing and fetching

When a skill says to publish to the issue tracker, create the appropriate file under `.scratch/<feature-slug>/`. Do not create a GitHub issue as a side effect.

When a skill says to fetch a ticket, read the referenced file. Resolve a ticket number within its named feature or effort; numbers can repeat across directories.

## Wayfinding operations

Wayfinder keeps a map at `.scratch/<effort>/map.md` with Notes, Decisions-so-far, and Fog sections. Its child tickets live at `.scratch/<effort>/issues/<NN>-<slug>.md` with the question in the body.

- Record each child's `Type:` as `research`, `prototype`, `grilling`, or `task`.
- Wayfinding child tickets use lifecycle values in `Status:`: `open`, `claimed`, or `resolved`.
- Record dependencies as `Blocked by: NN, NN`. A child is unblocked when every listed child is resolved.
- Select the lowest-numbered open, unblocked, unclaimed child.
- Set `Status: claimed` and save before starting work.
- To resolve a child, append `## Answer`, set `Status: resolved`, and add a short finding with a link to the map's Decisions-so-far section.

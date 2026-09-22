# Triage labels

Use the default vocabulary for ORelay's local Markdown issue tracker.

| Canonical role | Tracker label | Meaning |
| --- | --- | --- |
| `needs-triage` | `needs-triage` | A maintainer needs to evaluate the ticket. |
| `needs-info` | `needs-info` | More information is required. |
| `ready-for-agent` | `ready-for-agent` | Fully specified and ready for an unattended agent. |
| `ready-for-human` | `ready-for-human` | Requires human implementation. |
| `wontfix` | `wontfix` | Will not be actioned. |

When a skill refers to a triage role, use its tracker label in the ticket's `Status:` line. Wayfinding child tickets use the separate lifecycle convention described in `docs/agents/issue-tracker.md`.

Edit this mapping if the project's vocabulary changes.

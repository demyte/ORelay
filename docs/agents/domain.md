# Domain docs

ORelay uses a single-context layout. The domain glossary belongs in `CONTEXT.md` at the repository root. Architecture decision records belong in `docs/adr/`.

## Before exploring

Read `CONTEXT.md` and any ADRs relevant to the area being investigated.

If these files do not exist, proceed silently. Do not flag their absence or suggest creating them upfront. The `domain-modeling` skill creates them as terms and decisions are resolved, including when invoked through `grill-with-docs` or `improve-codebase-architecture`.

## Vocabulary

Use the glossary's terms in ticket titles, proposals, tests, and documentation. Do not replace defined terms with synonyms the glossary avoids.

If a needed concept is missing, reconsider whether it belongs in the project. Note genuine gaps for `domain-modeling`.

## Decision conflicts

If a proposal conflicts with an existing ADR, identify the ADR and explain the conflict. Do not silently override it.

# Documentation policy

Write maintenance documentation in English. Keep the English [README](../../README.md) and [Simplified Chinese README](../../README.zh-Hans.md) aligned when user-facing steps or consequences change. [README.en.md](../../README.en.md) routes readers to those versions. Maintain English and Simplified Chinese text for the tray's supported UI locales. Preserve the language of quoted source material and captured evidence.

| Location | Purpose |
| --- | --- |
| [CONTRIBUTING.md](../../CONTRIBUTING.md) | Human setup and contribution entry. |
| `docs/specs/` | Current implemented behavior, ownership, compatibility and limitations, with pointers to code and tests. |
| `docs/policies/` | Reusable maintenance and contributor procedures. |
| ADRs | Lasting decisions, rationale, active constraints and replacement relationships. |
| `docs/references/` | Provider contracts, versioned sources, validation procedures and existing observations. |
| `docs/evidences/` | Dated, sanitized evidence when raw observations need to be retained. |

Update the current spec and affected user guidance in the same change as the behavior. Label a proposal, a provider capability and a measured result according to what each establishes. Cite official provider documentation, versioned packages or immutable source revisions for precise contracts. Record the source, tool or artifact version, procedure, environment and limits for dated observations. Preserve original bytes where they matter and keep their `.gitattributes` handling intact. Sanitize credentials, private material, secret-bearing errors and personal machine paths before retaining evidence.

Record a lasting tradeoff in an ADR when its rationale will matter to future changes. Mark decisions active or superseded, retain the rationale for replaced decisions and link each successor and predecessor. Keep a partially amended decision active and identify exactly which constraint changed. Preserve identifiers and inbound links when moving or superseding records.

Write UTF-8, keep repository links and anchors valid and link to the canonical owner rather than copying it. Keep task plans, review ledgers, logs and other transient outputs in ignored workflow state or outside the checkout. Keep personal machine paths, local tool state and conversation transcripts out of maintained documentation.

## Instruction progressive disclosure

Keep the shared workflow, architecture and security boundaries in [AGENTS.md](../../AGENTS.md). Put conditional procedures in their canonical policies or specifications. Applicable ancestor and local instructions accumulate. `CONTRIBUTING.md` remains the human setup entry.

Every conditional owner must be directly reachable from the applicable instructions, with a trigger that names the operation and when to read it. Read the owner before starting that work and reuse it while it remains current. Keep complete procedures together and keep the common instructions usable on their own. When changing the layout, check entry-to-owner links, triggers, overlapping obligations and preserved historical pointers.

## Changelog

Describe cumulative net user-visible changes since the latest published stable release. Verify the published baseline before editing, and preserve the content of published sections. Keep behavior history in the changelog and implementation details in the relevant specification or decision record.

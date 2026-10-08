# UI hierarchy continuation — 2026-10-08

User resumed the paused UI work. This report supersedes the paused checkpoint's
UI findings; it does not accept the migration or authorize a release.

## Corrections

- Pages now have one main heading and a page-specific introduction. Recommendations
  and Experiment have distinct section headings instead of repeating the page name.
- Recommendation detail and presentation controls are separate labelled groups.
  Filters align and wrap; cards separate headings, badges, explanation, values and
  selection. Enum values and counts no longer acquire a generic count suffix.
- A saved baseline no longer offers another initial-measurement form. Experiment
  groups the current state with its next step; step rows use three, six or two
  columns according to available width. Active measurement uses one progress block.
- Unavailable mutation reasons appear once. History keeps its redundant caption
  accessible without repeating it visually. Setup and Results have shared group
  spacing and aligned label/value presentation.

## Rules for subsequent development

[UI design rules](../ui-design-system.md) define heading hierarchy, spacing,
control grouping, state/action relationships, navigation and evidence requirements.
`AGENTS.md` requires reading them before UI changes or reviews. The installed
`spacing-audit` skill loads the project document, keeping project rules in one
versioned source rather than duplicating them in the global skill.

## Evidence and checks

The local paired gallery is `.cache/e2e/ui-hierarchy-20261008/comparison.html`.
Its README records fixtures, asset hashes, findings and limits. Before is checkpoint
`b996091`. The comparison includes all six tabs, empty/populated data, RU/EN,
light/dark, and three CSS viewports; extra captures cover recommendation display
variants and measurement states. Fixtures are synthetic and do not mutate a real
qBittorrent instance.

Frontend type checking, 125 unit tests and production build passed. The standard
four browser tests pass with the existing environment's Playwright loader
workaround. Numeric spacing/overflow checks supplement visual inspection; they do
not replace it.

Native Windows DPI, file dialogs, real-client acceptance and the interrupted
30-minute native load gate remain unaccepted. The staged candidate predates these
source changes; the release binary is unchanged. Temporary disabled Rollup tree
shaking remains a build limitation. G2–G5 remain incomplete.

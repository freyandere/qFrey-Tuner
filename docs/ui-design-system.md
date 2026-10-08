# UI design rules

These rules govern qFrey-Tuner UI changes and visual reviews. Apply them to the
React interface and its native host. Existing defects are not design precedents.
Migration acceptance and release gates remain in [verification](migration-v2/verification.md).

## Change and review sequence

1. Read the affected screen, its callers and state guards. Identify its current
   step, available action and completed states before changing the layout.
2. Show the audit before spacing fixes: name the affected groups, current values,
   proposed values and intentional exceptions. For a full UI review, include
   heading repetition, control grouping, state/action conflicts and navigation.
3. Reuse the house components in `frontend/src/components/base.tsx` and the shared
   CSS in `frontend/src/styles/app.css`. Keep state and mutation guards intact.
4. Review actual populated screens after the change, using the same data, locale,
   theme and viewport for before/after captures. Fix observed defects before
   declaring the affected screens accepted.

## Page hierarchy

- Render one visible `h1` for the current page. A repeated eyebrow adds no level
  of meaning and is omitted.
- Use the page introduction to explain that page's task. Overview, Setup,
  Recommendations, Experiment, Results and History each have their own copy.
- An `h2` names a distinct section: for example, **Settings to review** under
  **Recommendations**, and **Measurement cycle** under **Experiment**.
- An `h3` names a subsection or recommendation. An `h4` names a result inside a
  result category. Keep heading levels in order.
- Keep each explanation once beside the state or control it explains. Keep a
  redundant table caption accessible without repeating it visually.

## Spacing and layout

Use an 8 px structural rhythm; 4 px half-steps are reserved for component internals.
The `spacing-audit` scale is 0, 4, 8, 12, 16, 24, 32, 40, 48, 56, 64, 80 and 96 px;
20 px is an allowed internal exception. Structural 20/28 px gaps require an
explicit reason rather than becoming a new default.

| Relationship | Default |
| --- | --- |
| Main content inset | 32 px; narrow layout 24 px vertical / 16 px horizontal |
| Card padding | 24 px |
| Sections and unrelated control groups | 24 px |
| Cards in a list and field rows | 16 px |
| Section heading to content | 16 px |
| Label to input; buttons within one action group | 8 px |
| Related paragraphs within a component | 12 px |
| Inline badge/icon spacing | 4–8 px |
| Table cell padding | 12 px |

Borders, focus outlines, optical offsets and automatic/fill-driven spacing are
intentional exceptions. Preserve spacing tokens. Change existing inline spacing
to a shared class when the same relationship repeats across the interface.

Controls that answer different questions belong to separate labelled groups.
Basic/Advanced chooses detail; Cards/Table chooses presentation. Show the selected
choice through styling and its accessible state. Place recommendation filters in
an aligned responsive group; keep search, category and visibility individually
labelled when the group wraps.

Within a recommendation card, group title and badges, explanation, current/proposed
values, and selection separately. Keep labels aligned with their values. Numeric
counts and enum/boolean/interface values have no generic `count` suffix; display
physical units only when supported by the value's contract.

Group Setup fields by network, hardware and usage. Give legends and groups their
own spacing. Results use aligned label/value rows and separated categories.
History uses one visible section title followed by the table or empty state.

Cycle steps form complete rows: three columns at normal desktop widths, six at
wide widths, and two at small widths. Keep order and the current-step indication.
At a narrow width, wrap control groups and stack label/value rows before clipping
text or causing page overflow. Long endpoints, API keys and identifiers may wrap;
wide tables scroll within their labelled region.

## State and action hierarchy

- Place the current state, its explanation and next action together. Distinguish
  preparation, measurement, plan review, application, after-measurement and results.
- Once a valid baseline is saved, show its status and next step. The initial
  measurement form and Start baseline action are absent. During an active
  measurement, show progress and cancellation instead of another start form.
- Show an after-measurement action only for the applicable state. Preserve frozen
  workload identity, validated target and verified-apply requirements.
- Completed, rolled-back and recovery states explain their next step. Their layout
  must not suggest that an obsolete start action is available.
- Show each distinct reason for unavailable actions once in the action block.
  Keep the affected actions disabled; deduplicating text does not change authority.
- Review and commit remain separate for settings, workload, lifecycle and network
  operations. Confirmations retain exact target, subject, expiry and backend state.
- Unknown measurements remain unknown. Keep scope and historical/current context
  beside the data; a visual change must not imply a real-client or performance pass.

## Navigation and accessibility

- Keep offline navigation available. Empty Recommendations and Results explain the
  prerequisite and offer an appropriate next step; History remains reachable.
- Preserve valid and incomplete Setup input across tab changes. Apply only the
  latest History response when requests overlap.
- Mark the current navigation item and move keyboard focus to the page heading.
  Keep keyboard operation, visible focus and accessible control names.
- Use native controls through existing house components. Checkbox labels provide
  the clickable area; the checkbox itself does not create an oversized vertical gap.
- Use the existing light/dark theme tokens, fonts, borders and contrast. Localize
  headings, states, filters, actions and confirmations in both RU and EN.

## Acceptance evidence

Review all six navigation pages for a shared layout change. For a local change,
review the affected pages and any callers that render the same component.

- Cover RU/EN, dark/light, and 960×680, 1200×840 and 1600×1000 CSS viewports.
- Use populated Recommendations in Basic/Advanced and Cards/Table, and Experiment
  with draft, saved baseline, active operation, applied/comparable, unavailable
  workload, completed and recovery states relevant to the change.
- Inspect hierarchy, group spacing, text wrapping, selected controls and the actual
  next action manually. Check overflow, focus, stale state and input retention with
  the existing tests where applicable.
- Save paired screenshots and a short report of the actual corrections and remaining
  defects. Passing a numeric spacing or overflow check alone does not accept the UI.
- Report native WebView2, dialog and real Windows DPI evidence separately. Browser
  viewport emulation covers CSS layout, not Windows scaling or native dialogs.

Change these rules explicitly when a new design decision is required; record its
reason and affected screens. A local exception must name its selector and reason
in the audit, rather than silently redefining the shared rhythm.

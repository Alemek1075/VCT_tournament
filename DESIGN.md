# VCT Hub design system

Source of truth: `src/VctHub.Web/wwwroot/css/site.css` (MVC) and `spa/src/index.css` (React SPA). Both use the same tokens.

## Theme
Dark only. Scene: a fan on a second monitor beside a stream, at night.

## Color (OKLCH, hue 20 tint on every neutral)
| token | value | use |
|---|---|---|
| `--ink` | oklch(0.155 0.006 20) | page |
| `--ink-2` | oklch(0.195 0.008 20) | panels, inputs |
| `--ink-3` | oklch(0.245 0.010 20) | hover, raised |
| `--line` | oklch(0.31 0.012 20) | borders |
| `--paper` | oklch(0.95 0.010 80) | text |
| `--muted` | oklch(0.70 0.012 60) | secondary text |
| `--red` | oklch(0.67 0.22 22) | actions, live, selection |
| `--teal` | oklch(0.82 0.12 175) | wins, success |
| `--gold` | oklch(0.78 0.09 88) | premium, captains |

Strategy: restrained on data pages, committed red on the home hero.

## Type
- Display: Big Shoulders Display 800/900, uppercase, hero words only.
- Headings, labels, buttons: Barlow Condensed 600-800, uppercase.
- Body and data: Barlow 400-600, `tabular-nums` for scores and stats.

## Shape
Hard corners. Primary buttons and cards get a cut corner (`clip-path`). Frame buttons have a 1px outline that tightens on hover.

## Motion
- `--ease: cubic-bezier(.16,1,.3,1)` (expo out); `--ease-riot: cubic-bezier(.06,.81,0,.98)`.
- Hover/press 150-250 ms, wipes and reveals 350-500 ms.
- Button hover: skewed fill wipes in from the left. Press: scale .97.
- Nav: one red indicator glides to the hovered link.
- Cards: cursor spotlight, sibling cards recede, logo tilts.
- Scroll reveal with stagger; cross-document view transitions between pages.
- Everything collapses to instant under `prefers-reduced-motion`.

## Components
Button (red, ghost, frame), tag/chip, panel, match row, team card, roster card, table, pager, toast, command palette (Ctrl+K), skeleton.

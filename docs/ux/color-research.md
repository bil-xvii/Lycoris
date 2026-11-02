# Color: research and proposal (02.11.2026)

Owner: «немножечко добавить цвета, но при этом не терять профессионализм». Before: black/white/grey + dark red (brand, risk, danger, live).

## What was read (web, short)

| Source | Takeaway used |
|---|---|
| GitHub Primer, [UI color system](https://primer.style/foundations/color/overview) (dark values via community copies: success #3fb950, attention #d29922, danger #f85149, accent #1f6feb) | Roles, not hues: success / attention / danger / accent; every role has a *muted* background and an *emphasis* border/fill; muted bg + border for subtle emphasis. |
| Microsoft [Fluent 2 color](https://fluent2.microsoft.design/color) | Semantic (status) colors build on real-world associations (red danger, yellow caution, green positive); in dark mode the shared palette shifts in saturation/brightness; use tokens only. |
| Linear, [How we redesigned the Linear UI](https://linear.app/now/how-we-redesigned-the-linear-ui) (+ third-party summaries) | Near-monochrome UI, one accent, hierarchy by lightness/weight; themes generated in LCH so equal lightness looks equal across hues; fewer, calmer colors = less noise. |
| Vercel Geist (design.md; third-party reconstructions) | Brand blue is a link/focus color, never a big fill; near-black canvas, grey scale does the structure. |
| Generic dark-mode guides ([Zeplin](https://blog.zeplin.io/dark-mode-color-palette), [UXMagic](https://uxmagic.ai/blog/dark-mode-ui-design-guide), [open-design](https://open-design.ai/skills/dark-mode-design/)) | Desaturate and lift status colors for dark (do not reuse light values); elevation via lighter surfaces; semantic tokens; verify contrast in real components. |
| [WCAG 1.4.1 / 1.4.3 / 1.4.11](https://www.w3.org/WAI/WCAG21/Understanding/non-text-contrast) (+ WebAIM, Deque) | Color is never the only carrier (label/icon next to it); text 4.5:1; meaningful graphics (progress fill, badge border, dot) 3:1. A badge whose text carries the meaning only needs the text ratio. |
| Grafana / Datadog threads ([Grafana issue 18041](https://github.com/grafana/grafana/issues/18041), [Datadog semantic palette](https://docs.datadoghq.com/dashboards/guide/compatible_semantic_tags/)) | Red/green side by side is the classic accessibility failure; blue/orange (blue/amber) pairs are safer; add a symbol/thicker border for failed states; semantic palettes map error=red, success=green but mean nothing once series are grouped, so charts use a neutral hue. |

Honest limits: the search tool returned mostly secondary summaries; Primer/Geist/Fluent hex values were not verified against their official repos and were used only as orientation. Final values were chosen and checked by our own contrast test.

## Conclusions

1. Professional dark tools use **roles, not rainbows**: neutral structure + 3 status roles (positive / caution / informational) + one danger. Hue count in view at once stays at 3-4.
2. Status color lives in **small things**: dot, 1 px outline, text, thin line, progress fill. Backgrounds are **very dark tints** of the hue (muted bg) with an outline of the hue (emphasis border) — never saturated fills.
3. Dark mode needs **desaturated, lighter text variants** (4.5:1) and a mid-tone fill for graphics (3:1) — two tokens per hue, not one.
4. Brand red + green + amber looks like a traffic light when all three sit side by side at the same weight. Avoid it by: **steel blue as the neutral "info" hue** (so most colored items are blue/green, not red/amber), amber only for "needs a look", red only for danger/failure/live/brand, and never more than one hue per component.
5. Charts/meters: one calm hue (steel blue) by default; amber/red only when a threshold is crossed, and the number is always printed next to the bar.
6. Gradients: not needed. Segmented bars and thin top-lines give the same "alive" feeling without looking like a gaming skin.

## Proposal (implemented)

Structure stays black/white; white fill = clickable. Red = brand, danger, failure, live. Three new muted hues, each with `Fill` (dot/bar/outline, ≥3:1), `Text` (≥4.5:1 on Base/Surface/Surface2/Surface3 and on its own Deep) and `Deep` (very dark tint, luminance ≤0.02, always with a 1 px `Fill` outline):

| Hue | Meaning | Fill | Text | Deep |
|---|---|---|---|---|
| Success (muted green) | applied, OK, done, safe, recommended, toggle ON | #2F8F5B | #58BE86 | #0B2016 |
| Warning (amber) | needs attention (not a failure), partial, pending, busy | #B8893A | #E0AA4A | #2A1E08 |
| Info (steel blue) | information, optional, progress, charts, measuring | #4F7FB0 | #78A6D6 | #0C1926 |
| Red (existing) | danger, expert, failed, live, brand | #B3121F | #EA5B63 | #45080E |

Risk vocabulary: БЕЗОПАСНО green, ПО ЖЕЛАНИЮ steel, ЭКСПЕРТ red (amber is not used for it, so it does not read as a traffic light).
Before/after: `color-before-after-*.png`; all pages: `color-preview.png`. Enforced by `tests/Lycoris.Tests/ThemeContrastTests.cs`.

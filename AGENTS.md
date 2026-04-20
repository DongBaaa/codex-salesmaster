## Scope of this file

- This global `AGENTS.md` is for MCP routing, UI consistency, and broad workflow defaults.
- Hard execution settings such as model, reasoning effort, agent limits, and mandatory spawn parameters belong in `config.toml`.
- General work process, verification, and completion standards belong in `codex.md`.

## MCP Routing

- Use the most relevant MCP first. Do not call many MCP servers unless needed.
- For OpenAI API, ChatGPT, Codex, Apps SDK, and Responses API questions, use `openaiDeveloperDocs` first.
- For login, clicking, typing, navigation, and browser actions, use `playwright`.
- For webpage content extraction, link collection, price extraction, and multi-page information gathering, use `scrapling`.
- For local file read/write inside the project, use `filesystem`.
- For GitHub repositories, issues, PRs, code, and workflows, use `github`.
- For database queries and schema inspection, use `postgres`.
- Prefer the simplest tool that directly fits the task.

## Global preflight rules

- For every non-trivial change, first inspect affected screens, data scope, tenant/office rules, sync impact, and live deployment impact.
- If there is any blocker, ambiguity, regression risk, or likely data 꼬임, report it to the user first before editing or deploying.
- Only proceed after explicitly concluding either `진행 가능` or describing the blocking issue.
- Never assume asset scope, billing scope, item scope, and tenant scope are the same. Check each separately before changing queries or permissions.
- For scope-related changes, verify at minimum: 자산 조회, 품목관리, 청구/전표, 동기화/dirty, 권한 저장 가능 여부.

# Global UI rules

## Design system rules
- For any UI work, keep spacing, typography, colors, radius, shadows, borders, and reusable components consistent with the existing design system.
- Reuse existing shared components before creating new ones.
- Prefer existing tokens, variables, and utility classes over one-off values.
- Do not introduce random colors, spacing values, radius values, or shadows unless clearly necessary.
- Keep button hierarchy, form fields, tables, cards, drawers, and modal patterns consistent across screens.

## B2B dashboard layout rules
- For admin, ERP, sales, purchase, inventory, reporting, or office tools, optimize for desktop-first workflows.
- Prefer layouts in this order when appropriate:
  1. summary cards
  2. search and filters
  3. main data table
  4. detail panel or drawer
- Prioritize workflow speed, scanability, density, and readability over decorative visuals.
- Use clear action priority:
  1. save
  2. edit
  3. delete
  4. export or print
- Avoid decorative UI that reduces information density or slows down work.

## Output style for UI changes
- When suggesting UI changes, explain the screen goal, the main user actions, the recommended section order, and why the layout fits office work.
- Preserve workflow efficiency first and visual polish second.

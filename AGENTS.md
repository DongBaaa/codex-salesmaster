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

## Patch completion rule

- When a requested patch is implemented and verification passes, complete the same work item with live deployment, git commit, and git push by default.
- If tests fail, operational checks fail, deployment impact is unclear, or the change could cause data 꼬임, report the blocker first and do not deploy or push until it is resolved.
- For desktop app patches, bump the desktop version and regenerate the installer/update package before live deployment so the live manifest and installed client can receive the patch.

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

## Linux PC 운영 작업 안전 규칙

- 거래플랜, 워크플랜, itw 홈페이지 작업은 한 번에 하나의 서비스만 진행합니다. 서로 다른 서비스의 수정/배포/재시작을 같은 작업 흐름에 섞지 않습니다.
- 현재 거래플랜 서버 본체는 NAS가 아니라 Linux PC `itw@192.168.0.199:2222`의 `/srv/georaeplan` 기준으로 운영합니다.
- live 반영 전에는 `trade.2884.kr` 접속 상태와 Linux PC의 거래플랜 Docker/compose, systemd, nginx/Reverse Proxy, PostgreSQL 상태 영향 여부를 확인합니다.
- 공통 인프라 영향 가능성이 있으면 `work.2884.kr`, `itw.2884.kr` 접속 상태도 함께 확인합니다.
- Docker 전체 재시작/정리 명령은 사용하지 않습니다.
  - 금지 예: `docker compose down`, `docker system prune`, `docker container prune`, `docker image prune`, `docker volume prune`, `docker stop $(docker ps -q)`, `docker restart $(docker ps -q)`, `sudo reboot`, `sudo systemctl restart docker`.
  - 허용 방향: Linux PC의 `/srv/georaeplan` 거래플랜 compose project 안에서 필요한 서비스만 명시해 `api`, `postgres` 단위로 작업합니다.
- Linux PC의 Docker daemon, systemd 전체 서비스, nginx/Reverse Proxy 전체 재시작, PostgreSQL 전체 재시작은 다른 서비스까지 영향을 줄 수 있으므로 사용자에게 먼저 보고하고 승인받기 전에는 진행하지 않습니다.
- live 반영 후에는 `trade.2884.kr` 상태를 우선 확인하고, 공통 인프라 영향 가능성이 있으면 `work.2884.kr`, `itw.2884.kr` 상태도 확인하여 Linux PC/네트워크 장애를 조기에 발견합니다.
- `tools\nas`와 legacy NAS 런북은 과거 호환/참고용입니다. 새 운영 작업에서는 `tools\linux`와 Linux PC 기준 런북을 우선 사용합니다.

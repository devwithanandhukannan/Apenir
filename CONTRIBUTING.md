# Contributing — Git & GitHub Guidelines

This document describes the recommended branching, commit message style, and PR workflow for the Apenir backend repository.

Branching strategy

- `main` — always production-ready, protected branch. Only merge release/hotfix PRs here.
- `develop` — integration branch for ongoing development (optional). All completed features may merge here before release.
- `feature/<ticket-number>-short-description` — feature branches off `develop` (or `main` when `develop` is not used).
  - Example: `feature/JIRA-123-add-auth-service` or `feature/123-add-auth-service`.
- `bugfix/<ticket>-short-desc` or `hotfix/<ticket>-short-desc` — for urgent fixes; branch from `main` and create PR back to `main` and `develop`.
- `release/<version>` — used to prepare a release from `develop` into `main`.

Branch naming rules

- Use lowercase, hyphens for separators.
- Start with a type prefix: `feature/`, `bugfix/`, `hotfix/`, `release/`, `chore/`, `docs/`.
- Include a ticket number if available.

Commit message style

We use Conventional Commits (https://www.conventionalcommits.org/):

Format:

<type>(<scope>): <short description>

- `type`: feat, fix, docs, style, refactor, perf, test, chore, ci
- `scope`: optional, e.g., `Auth`, `API`, `Booking`
- `short description`: imperative, present tense, < 72 chars

Examples:

- `feat(Auth): add JWT refresh token endpoint`
- `fix(Payment): handle null gateway response`
- `docs: update contributing guide`
- `chore(deps): bump MongoDB.Driver to 2.25.0`

When the change includes a breaking change, add an extra footer:

BREAKING CHANGE: description of what changed and migration/upgrade steps.

Pull Request (PR) workflow

1. Create a branch from `develop` (or `main` when appropriate):

```bash
git checkout -b feature/123-add-auth-service
```

2. Make focused commits following Conventional Commits.
3. Push the branch:

```bash
git push -u origin feature/123-add-auth-service
```

4. Open a PR targeting `develop` (or `main` for hotfixes). Use the PR template, link the issue/ticket, and add reviewers.
5. Ensure CI passes and address review comments. Squash or rebase as needed before merge if the repo policy requires a clean history.
6. Merge using GitHub UI (prefer "Merge" or "Squash and merge" according to project settings). For releases/hotfixes merge into `main` and create a tag.

Code review checklist

- Does code compile and pass tests?
- Is the scope of the change minimal and documented?
- Are sensitive values kept out of the repo and stored in secrets/env?
- Are new dependencies justified and minimal?
- Are interfaces and DTOs well-defined and documented?
- Are there unit/integration tests where appropriate?

Commit hygiene and signing

- Rebase or pull the latest `develop` before opening a PR to reduce conflicts.
- Keep commits focused and logically grouped.
- Optionally sign commits with `git commit -S` if required by the repository.

Tips & Helpers

- Create small, iterative PRs — they're easier to review.
- Use `git rebase -i` to squash noisy WIP commits before merging.
- Use `gh` (GitHub CLI) to create PRs quickly:

```bash
gh pr create --base develop --head feature/123-add-auth-service --title "feat: add auth service" --body "Implements ..."
```

Respect and etiquette

- Leave constructive, actionable review comments.
- Be timely with reviews where possible.
- Assign reviewers, and add context in the PR description.


Thank you for contributing — follow these guidelines to keep the repository healthy and collaborative.

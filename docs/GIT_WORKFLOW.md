
# Git & GitHub: Quick Workflow for Apenir Backend

This quick-start shows common commands and the recommended PR flow.

Clone the repository

```bash
git clone git@github.com:<org-or-user>/Apenir.Backend.git
cd Apenir.Backend
```

Setup upstream (if you forked)

```bash
git remote add upstream git@github.com:<org-or-user>/Apenir.Backend.git
git fetch upstream
```

Create a new feature branch

```bash
git checkout develop        # or main if develop not used
git pull origin develop
git checkout -b feature/123-add-auth-service
```

Work locally, stage, and commit

```bash
git add .
git commit -m "feat(Auth): add jwt token refresh endpoint"
```

Keep your branch up to date

```bash
git fetch origin
git rebase origin/develop
# or merge
# git merge origin/develop
```

Push branch to remote and open a PR

```bash
git push -u origin feature/123-add-auth-service
# Open PR on GitHub UI or use gh
gh pr create --base develop --head feature/123-add-auth-service --title "feat(Auth): add refresh token" --body "Implements refresh token flow"
```

Merging

- Wait for CI checks and approvals.
- Use "Squash and merge" for tidy history (when needed) or regular "Merge" if preserving commit history.
- Delete the branch after merge via GitHub UI or CLI:

```bash
gh pr merge --squash
git push origin --delete feature/123-add-auth-service
```

Hotfix / Production fix

```bash
git checkout main
git pull origin main
git checkout -b hotfix/456-fix-payment-null
# implement fix
git commit -m "fix(Payment): handle null response"
git push -u origin hotfix/456-fix-payment-null
# open PR to main and back-merge to develop
```

Using GitHub Issues and linking

- Reference issues or ticket numbers in commits and PR descriptions: `Fixes #123` or `Refs JIRA-456`.
- This will auto-link and close issues when PRs are merged.

CI / Checks

- All PRs must pass CI (unit tests, linters, build) before merging.
- Fix failing checks and push changes; PR updates automatically.

Backporting and releases

- When making a release, create a `release/x.y.z` branch from `develop`, finalize versioning, bump dependencies, run full test matrix, then create a release PR to `main` and tag.

Further reading

- Conventional Commits: https://www.conventionalcommits.org/
- GitHub Flow: https://docs.github.com/en/get-started/using-git/about-branches


# Contributing

Use [Conventional Commits 1.0.0](https://www.conventionalcommits.org/en/v1.0.0/) for commit messages:

```text
<type>[optional scope]: <short description>
```

Use `feat` for a new feature and `fix` for a bug fix. Other useful types include `docs`, `test`, `build`, `refactor`, and `chore`. A scope can name the affected part of this project, such as `widget`, `helper`, or `auth`.

Examples:

```text
feat(widget): add a My tasks filter
fix(helper): preserve Planner task order
docs: explain Microsoft sign-in setup
```

Mark a breaking change with `!` after the type or scope, or add a `BREAKING CHANGE:` footer. Keep separate features and fixes in separate commits when practical.

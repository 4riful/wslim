# Contributing

The most useful contribution is a new cleanup rule. Open an issue with the "Suggest a cleanup rule" template, or send a
pull request that adds the rule to `src/agent.sh` and `docs/RULES.md` (see "How to add a rule" there).

A rule is accepted when it is clear what the folder holds, how it comes back, and that it never contains logins,
databases or user work. When in doubt it goes in the `review` tier.

Before a pull request: `sh -n src/agent.sh`, `shellcheck -s sh -S warning src/agent.sh`, and `build.cmd` (C# 5 only).

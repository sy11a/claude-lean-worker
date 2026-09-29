# Project notes for workers

<!--
Every worker receives this file as part of its system prompt, so every line costs tokens on
every worker API call. Keep it to what a worker needs to change code correctly, and link to
longer documents by path instead of pasting them. Target: under ~2k tokens.
Fill it in together with the orchestrating session, then edit it by hand. It is yours.
-->

You are a worker process started by an orchestrating session. You get one task. You finish it,
or you report why you could not. Your final message is the report the task asks for.

## Stack
- Languages / frameworks: <...>
- Repository layout: <where the main code, tests and tools live>

## Commands
- Build: `<command>`
- Test (all): `<command>`
- Test (one project / file): `<command pattern>`
- Lint / format: `<command>`

## Conventions that matter for changes
- <rule>
- <rule>

## Never
- Commit, push, or create branches.
- Edit CI, dependency versions, secrets or environment configuration unless the task says so.
- <product-specific prohibitions>

## Where to look
- <doc or folder>: <what it answers>

## Work style
- Read only the files you need. Prefer Grep and Glob to reading whole directories.
- Keep command output short, e.g. filter test output to failures.
- If the task is ambiguous or needs a decision it does not cover, stop and say so in the report.

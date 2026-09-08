---
name: write-and-execute-tools
description: "Select, verify, generate, or run local translation pipeline tools. Use when PDF conversion, OCR, rendering, font, validation, or provider capabilities are missing."
---

# Write And Execute Tools

## Decision Order

1. Probe installed commands with `Get-Command` and their native version flags.
2. Prefer an installed open-source tool matching `.github/translation-pipeline.defaults.json`.
3. If adoption is needed, verify the official HTTPS source, license, pinned version, Windows support, and noninteractive usage. Keep the adapter replaceable.
4. Ask the user before a package download, machine-wide install, PATH change, destructive action, external API call, or source-content transfer.
5. If approved, prefer a repository-local environment/cache over a machine-wide install.
6. Generate only a focused parameterized PowerShell adapter in `.github/tools/`; never regenerate command sequences in prose when a repeatable script is warranted.

## Safety

- Never write secrets to scripts, config, state, URLs, arguments, or logs. Read credentials only from an approved runtime secret mechanism.
- Never execute downloaded code without source/license/version verification.
- Do not build a translation-provider adapter until the user selects and authorizes a provider. Local model-driven chunk translation needs no provider file.
- Parse-test generated PowerShell and exercise it against disposable data before using it on a job.
- Record public documentation used with `Add-TranslationResearchSource.ps1`; do not include source-book excerpts in web queries.

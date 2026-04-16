# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Project Purpose

Per `README.md`, this project is intended to translate Counter-Strike 2 in-game chat (primarily from Russian) so the user can read what other players are typing.

## Current State

The repository is effectively empty — only `README.md` exists at the initial commit (`b695b74`). There is:

- No source code
- No build system, package manifest, or dependency lockfile
- No tests, linter configuration, or CI
- No documentation beyond the one-line README

As a result, there are no build/lint/test commands to document yet. **Do not invent commands or architecture that don't exist in the tree.** When scaffolding is added, update this file with the real commands and structural notes (e.g., how the chat is captured from CS2, how translation is performed, how results are surfaced to the user).

## Platform Notes

- Primary development environment is Windows 11 with Git Bash (Unix shell syntax). Use forward slashes in paths and `/dev/null` rather than `NUL` when running shell commands.
- The target application (CS2) is a Windows game, so any chat-capture implementation will likely be Windows-specific (overlay, OCR of the chat region, game-state-integration, or log-file tailing) — keep this in mind when choosing libraries or languages.

## Design Questions Still Open

These should be resolved with the user before substantial implementation, since they shape the whole architecture:

1. **How is chat captured?** Options include screen/OCR of the chat overlay, reading CS2's console log, a game-state-integration (GSI) endpoint, or a separate overlay that hooks the game window.
2. **How is translation performed?** Local model vs. cloud API (Google Translate, DeepL, an LLM) — affects latency, cost, and offline capability.
3. **How are translations displayed?** In-game overlay, separate window, system notifications, or written back into the chat.
4. **Languages in scope.** README mentions Russian; clarify whether other languages are also needed.

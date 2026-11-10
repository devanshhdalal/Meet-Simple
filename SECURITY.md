# Security policy

Meet Simple is designed so **audio, transcripts, and summaries never leave the machine**, aside from optional one-time setup downloads the user explicitly approves, and an optional GitHub release check / update apply when “Check for updates” is on.

## Supported versions

Security fixes target the latest source on `main` and the newest published release when releases exist.

## What is in scope

- Unexpected network egress or data leaving the device  
- Bypass of download host allowlists, checksum verification, Ollama Authenticode checks, or app-update SHA-256 / zip-slip checks  

- Path traversal or unsafe handling of settings / model ids / output names  
- Supply-chain issues in dependencies we ship or restore  

## What is out of scope (by design)

- Requiring users to trust a local AI stack they chose to install (Whisper weights, Ollama, summary models)  
- Windows SmartScreen warnings on **unsigned** builds  
- “Attacks” that need the user to already run untrusted code with their own privileges  

## Reporting a vulnerability

Please **do not** open a public issue for exploitable vulnerabilities.

Email or privately message the maintainer (GitHub security advisory on this repository preferred once the repo is public): include a short description, impact, and steps to reproduce. We will acknowledge when we can and work on a fix before any detailed disclosure.

## Defense notes for contributors

See the Security section of [`AGENTS.md`](AGENTS.md). Do not add telemetry, PATH-resolved process launches, automatic redirect following in `Downloader`, or skipped signature/checksum checks without an explicit, reviewed reason.

# Migration Guide

Upgrade notes and breaking-change guidance between **Themia** versions. Every
**(breaking)** entry in [CHANGELOG.md](CHANGELOG.md) has a matching section here
with the *why* and concrete upgrade steps.

> **No releases yet** — there is nothing to migrate. The first published version
> will be `0.1.0`. This file establishes the convention and is filled in as
> breaking changes ship.

## How to read this guide

- Sections are ordered **newest first**, headed by the version that introduced the change.
- Each entry states: **What changed**, **Why**, and **How to upgrade** (before → after).
- Non-breaking changes are *not* listed here — see the CHANGELOG.

## Template

````markdown
## x.y.z

### <short title of the breaking change>

**What changed:** …

**Why:** …

**How to upgrade:**

- Before:
  ```csharp
  // old usage
  ```
- After:
  ```csharp
  // new usage
  ```
````

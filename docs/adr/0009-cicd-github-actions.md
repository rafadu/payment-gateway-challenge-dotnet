# ADR-0009: CI/CD pipeline on GitHub Actions

## Status
Accepted

## Context
The project is hosted on GitHub, so its CI/CD should use GitHub's native ecosystem (Actions,
Environments, CodeQL, Dependabot) rather than introducing an external CI platform for a system of
this scope. Payments code specifically justifies stricter gating than a typical service: a broken
deploy here has direct financial/reputational impact.

## Decision
A GitHub Actions pipeline with the following stages:

1. **Build** — restore and build the solution.
2. **Unit + component tests** — fast, no external dependencies; run on every push/PR.
3. **Architecture tests** (ADR-0008) — part of the same test run, so boundary violations fail the
   build immediately.
4. **Integration tests** — bank simulator, MongoDB, and RabbitMQ started as GitHub Actions service
   containers (or via `docker compose up` in the job); the integration suite (ADR from the main
   design doc) runs against the real containers.
5. **In-process performance tests (NBomber/BenchmarkDotNet)** — run on every PR if fast enough to
   keep CI responsive; otherwise on merge to `main`.
6. **k6 load/soak tests** — a separate, scheduled (nightly) or manually-triggered workflow against
   a full docker-compose stack, given their runtime and infrastructure cost don't belong on every
   PR.
7. **Static analysis / security scanning** — `dotnet format --verify-no-changes`, CodeQL (native
   to GitHub, no extra tooling), and Dependabot for dependency vulnerability alerts.
8. **Build & push container image** — on merge to `main`, build the API's image and push to GHCR.
9. **Deploy** — automatic to a staging environment; production gated behind a manual approval via
   GitHub Environments' required-reviewer protection. Given this is payment-critical
   infrastructure, a canary or blue-green rollout is the intended shape rather than a plain
   rolling deploy, though the exact mechanism depends on the target hosting platform and isn't
   specified further here.

All secrets (DB connection string, RabbitMQ credentials, KMS key references) are stored as
GitHub Actions encrypted secrets/environment secrets, never committed.

## Consequences
- Steps 2–3 gate every PR quickly (no external infra), keeping feedback fast for the common case.
- Steps 4–6 add real infrastructure dependency and runtime cost to CI, which is why they're
  scoped to PR-merge or scheduled runs rather than every push — a deliberate balance between
  thoroughness and iteration speed.
- Using GitHub-native tooling (CodeQL, Dependabot, Environments) avoids operating a separate CI
  platform for a system of this size, consistent with the "simpler, the better" preference
  applied elsewhere in these decisions.

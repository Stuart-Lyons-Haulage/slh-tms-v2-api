# SLH TMS API change control

_Last reviewed: 2026-09-16_

This repository is part of the live SLH TMS baseline. Future changes should be made as scoped amendments against this baseline, not as broad rewrites or layered compatibility patches.

## Change principles

1. Keep `main` as the live production baseline.
2. Use scoped branches and pull requests for normal amendments.
3. Do not merge unless API CI and CodeQL are green.
4. Do not auto-promote email-derived orders; staged planner approval remains mandatory.
5. Keep SQL as the authoritative operational master-data source.
6. Retain migration, schema repair and production data-resilience code unless proven unreferenced and no longer required.
7. Prefer additive, versioned contracts over breaking changes.
8. Add regression tests for order intake, planner, tracking, TachoMaster and wallboard changes.

## Required checks before merge

- .NET API build.
- Scheduled Jobs build.
- Power Automate info-mailbox intake workflow validation.
- Power Automate customer load-plan outbound workflow validation.
- xUnit test suite.
- CodeQL.

## Required branch protection

GitHub branch protection should be enabled for `main` with:

- require pull request before merging;
- require status checks to pass before merging;
- require branches to be up to date before merging;
- require CodeQL;
- block force pushes;
- block branch deletion;
- restrict direct pushes to `main`.

The current connector can read branch protection state but does not expose the required GitHub administration write action to enable it safely. Until enabled manually, direct pushes to `main` remain possible.

## Baseline reset rule

Before adding or amending functionality, identify the affected operational surface:

- order intake;
- staged approval;
- duplicate/amendment comparison;
- transport orders;
- planner/run control;
- live tracking/ETA;
- TV wallboard;
- master data;
- authentication/authorisation;
- deployment/jobs.

Then make the smallest scoped change that preserves the live baseline and add targeted tests.

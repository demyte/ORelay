---
name: release-orelay
description: Release ORelay when the user explicitly invokes $release-orelay, using a patch, minor, or major increment and checking GitHub release and NuGet publication.
---

# Release ORelay

Use this skill only when the user explicitly invokes `$release-orelay`. Do not select or invoke it automatically from a general release request. A request to inspect, plan, or edit this skill does not authorize publishing a release.

Run commands from the repository root. Read [versions and releases](../../../docs/releases.md) and the current [release workflow](../../../.github/workflows/release.yml). The CLI and `ORelay.Aspire.Hosting` share a version. Pushing `v<version>` starts the workflow that verifies, packages, and publishes them.

## Choose the version

| User invocation | Behavior |
| --- | --- |
| `$release-orelay` | Ask which part to release: patch, minor, or major. |
| `$release-orelay patch` | Increase the patch version. |
| `$release-orelay minor` | Increase the minor version. |
| `$release-orelay major` | Increase the major version. |

Accept `patch`, `minor`, or `major` as the release part. If the part is missing or unrecognized, ask one question offering those choices, with calculated versions when available. Wait for the answer before creating or pushing a tag. Do not infer a missing part from commits, a previous release, or an explicit version number.

Once the part is supplied, calculate and announce the exact tag and commit, then proceed without asking for the same approval again. Keep that version and SHA fixed for this invocation, including retries; do not calculate another increment after pushing the tag.

Refresh `origin/main` and tags with `git fetch origin --tags`, without force. Check the origin URL resolves to `demyte/ORelay`. Determine the base from the highest stable SemVer tag that exists on the remote and is reachable from `origin/main`. Compare numeric components, not strings or tag creation dates. Exclude prereleases and local-only tags from this base. Use `git ls-remote --tags --refs origin` and local ancestry checks after fetching.

If no stable tag exists, explain that the increment has no release baseline and ask how the user wants to establish the first version. Do not invent a baseline.

| Increment | Rule | Example from `0.1.7` |
| --- | --- | --- |
| Patch | Increase patch | `v0.1.8` |
| Minor | Increase minor, reset patch | `v0.2.0` |
| Major | Increase major, reset minor and patch | `v1.0.0` |

Do not derive the next release from a MinVer development stamp. Validate the selected tag against the rules in [get-version.ps1](../../../scripts/get-version.ps1): valid SemVer, no build metadata, and numeric components at most 65534. Explain any version collision or version below a newer stable release instead of silently selecting another version. When resuming this invocation, follow the existing-tag rules below.

## Prepare the selected commit

For a new tag, default to the fetched `origin/main` tip. When resuming this invocation, use its recorded commit. Honor an explicit commit only if it belongs to `origin/main`. Record its full SHA.

Before creating or pushing a tag, use a clean checkout of that exact SHA, because `get-version.ps1 -Tag` validates against `HEAD`. A clean `main` checkout may be advanced with `git pull --ff-only`; otherwise use an existing clean checkout or a run-owned worktree. Do not reset, stash, discard, or commit unrelated changes to manufacture a clean checkout. Remove only worktrees created by this release task after they are clean and no longer needed. Checking an already-pushed tag's publication does not require changing the checkout.

Check the latest `CI` and `Native platform artifacts` results for that exact SHA. Wait for running checks. Investigate failed checks before publishing. If checks have not run, use the repository's existing verification commands and workflow mechanisms; do not treat missing results as success. The release workflow runs its own checks again against the tag.

Inspect the selected tag on both local and remote refs before creating it:

- If absent, it can be created after the version and commit are settled.
- If it already points to the selected commit, reuse it. If already remote, find its existing release run rather than trying to trigger a second release by moving the tag.
- If it points to a different commit, stop and report the collision. Never force-push, delete, or move a release tag, or overwrite a published package.

Before mutation, state the tag, commit, and that the tag triggers GitHub release and NuGet publication. The user's explicit skill invocation with a supplied or subsequently chosen release part is sufficient authorization. For a planning-only request, report the proposed tag without creating it.

## Tag and publish

Set `$version`, `$tag`, and `$commit` from the validated choices. For a tag that does not already exist locally:

```powershell
git tag -a $tag $commit -m "ORelay $version"
if ($LASTEXITCODE -ne 0) { throw 'Could not create the release tag.' }
```

Run the existing validation before pushing, including when resuming with an existing local tag:

```powershell
$ErrorActionPreference = 'Stop'
$metadata = & ./scripts/get-version.ps1 -Tag $tag | ConvertFrom-Json
if ($metadata.version -cne $version -or $metadata.commit -cne $commit) {
    throw 'The selected tag, build version, and source commit do not agree.'
}
git push origin "refs/tags/$tag"
if ($LASTEXITCODE -ne 0) { throw 'Tag push failed; inspect the remote before retrying.' }
```

Push only the selected tag. Do not use `git push --tags`. If validation fails, do not push; report the failure and any local tag created. If push completion is uncertain, inspect the remote ref before retrying.

## Check publication

Find the `release.yml` run matching both the tag and full SHA, not merely the newest run. Follow it through tag validation, CI, all six native platforms, package-consumer checks, and publication. Use bounded waits and report meaningful progress.

After success, inspect the release and confirm its tag, draft/prerelease status, six versioned native archives with checksums, and matching `ORelay.Aspire.Hosting` package. Check the publication job's package-download verification; an uploaded `.nupkg` release asset alone does not prove feed publication. Use the [release verification recipe](../verify-orelay/features/versions-releases.md) when further artifact or consumer proof is needed.

For a failure, inspect the failed step and distinguish a pushed tag, a draft release, a published package, and a finalized release. Retry failed jobs only when the cause is understood and a retry can resolve it, preserving existing artifacts. Do not repeatedly retry unchanged failures, change tags, skip gates, or publish manually to bypass the workflow. Report any fix or user input needed.

Finish with the exact tag and commit, release/run links, verification result, and whether NuGet publication completed. Never call a tag push alone a completed release.

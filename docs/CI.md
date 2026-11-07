# CI runners and security

CI runs on the `bogware` organization's self-hosted runners. They are long-lived and shared with
other repositories, so a job that runs untrusted code could compromise more than this repository.
These rules prevent that.

## Which runner runs what

| Event | Runner | Why |
|-------|--------|-----|
| `pull_request` | GitHub-hosted | Runs code that hasn't been reviewed, so it gets a disposable machine |
| `push`, tags, `workflow_dispatch` | `[self-hosted, Linux, X64]` | Only people with write access can trigger these |

Release, package and image jobs run only on tags or `main`, never for a pull request.

## What enforces this

The `runs-on` expression is a default, not a security control. A pull request can edit the workflow
file it runs, including that expression. The controls that hold are:

- **Fork pull requests need approval.** The organization requires a maintainer to approve workflow
  runs from all external contributors (`all_external_contributors`). Read the diff, including
  anything under `.github/`, before approving.
- **The workflow token is read-only by default**, and jobs ask for only what they need. Workflows
  can't approve pull requests.
- **Only allowed actions run**, each pinned to a full commit SHA, which the repository enforces. A
  hijacked tag can't change what CI runs, which is how `trivy-action` was compromised in 2026.

## Job isolation on shared hosts

- `.github/actions/isolate-job` puts the .NET SDK, NuGet packages and scanner cache in the job's temp
  directory.
- `DOCKER_CONFIG` in the image job keeps registry credentials out of the runner user's home directory.
- `actions/checkout` runs with `persist-credentials: false`, so no token is left in `.git/config`.
- Service containers publish random host ports, so concurrent jobs can't collide.
- The scan image has a per-run tag and is removed afterwards.

## Updating actions

Updates are manual. To change an action, resolve the release tag to a commit, check it against the
project's security advisories, and pin it as `owner/action@<sha> # vX`. New third-party actions must
also be added to the repository's allowed list.

## Before making this repository public

Do these in order:

1. Enable branch protection on `main`, which requires a public repository on the current plan:
   require pull requests and passing checks.
2. Protect `.github/` with a CODEOWNERS rule, so workflow changes always get reviewed.
3. Scan the full history for secrets.
4. Confirm that fork pull request approval still reads `all_external_contributors` for this repository.

## Known limits

The runners are not ephemeral. A job that gains code execution on one could persist there and reach
later jobs from any repository sharing that runner. Approval and review are what stand in the way.
Removing this risk needs a dedicated runner group for this repository and runners registered with
`--ephemeral`, which requires access to the runner hosts.

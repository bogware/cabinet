# CI runners and security

CI runs on the `bogware` organization's self-hosted runners. They are long-lived and shared with
other repositories, so a job that runs untrusted code could compromise more than this repository.
These rules prevent that.

## Which runner runs what

All jobs run on `[self-hosted, Linux, X64]` while this repository is private. Only people with access to it can trigger them, and it has no forks. Release jobs cross-compile the Windows and macOS builds on Linux.

Release, package and image jobs run only on tags or `main`.

When the self-hosted runners can't be used, for example while one is out of disk, set the repository
variable `CABINET_RUNNER` to `github` (`gh variable set CABINET_RUNNER --body github`) and every job
runs on GitHub-hosted `ubuntu-latest`. Delete the variable (`gh variable delete CABINET_RUNNER`) to go
back. It applies to runs started after it changes; a re-run uses the workflow from the original
commit. It's an organization-level decision, not one a pull request can make, since pull requests
can't set repository variables.

Pull requests from forks don't run at all: `build-and-test` runs for a pull request only when its
branch is in this repository, and every other job needs it. Self-hosted runners keep state between
jobs and can reach Docker, so code from a fork must never reach them.

## What enforces this

Beyond the repository being private:

- **Fork pull requests are skipped** by the `if:` on `build-and-test`. A pull request can edit that
  condition, so approval below is the backstop.
- **Fork pull requests need approval.** The organization requires a maintainer to approve workflow
  runs from all external contributors (`all_external_contributors`). Read the diff, including
  anything under `.github/`, before approving.
- **The workflow token is read-only by default**, and jobs ask for only what they need. Workflows
  can't approve pull requests.
- **Only allowed actions run**, each pinned to a full commit SHA, which the repository enforces. A
  hijacked tag can't change what CI runs, which is how `trivy-action` was compromised in 2026.

## Job isolation on shared hosts

- `.github/actions/isolate-job` gives each job its own directory for the .NET SDK, NuGet packages,
  scanner cache and registry credentials, removed when the job ends. It's uniquely named because the
  runner's temp directory can hold leftovers from earlier jobs, and the .NET installer won't repair
  a partial install it finds there.
- `actions/checkout` runs with `persist-credentials: false`, so no token is left in `.git/config`.
- Service containers publish random host ports, so concurrent jobs can't collide.
- The scan image has a per-run tag and is removed afterwards.

## Updating actions

Updates are manual. To change an action, resolve the release tag to a commit, check it against the
project's security advisories, and pin it as `owner/action@<sha> # vX`. New third-party actions must
also be added to the repository's allowed list, including actions they call themselves. Because of
their compromise history, `trivy-action` and `setup-trivy` are allowed only at their exact commits, so
updating Trivy also means updating both entries in that list.

## Before making this repository public

Do these in order:

1. Run pull requests on GitHub-hosted runners, as the organization's public repositories do:
   `runs-on: ${{ github.event_name == 'pull_request' && 'ubuntu-latest' || fromJSON('["self-hosted", "Linux", "X64"]') }}`,
   and remove the fork condition on `build-and-test` so outside contributions get tested.
   Pull requests from forks must never reach a shared self-hosted runner. That expression is only a
   default, since a pull request can edit it, so fork approval below is what enforces this.
2. Enable branch protection on `main`, which requires a public repository on the current plan:
   require pull requests and passing checks.
3. Protect `.github/` with a CODEOWNERS rule, so workflow changes always get reviewed.
4. Scan the full history for secrets.
5. Confirm that fork pull request approval still reads `all_external_contributors` for this repository.

## Known limits

The runners are not ephemeral. A job that gains code execution on one could persist there and reach
later jobs from any repository sharing that runner. Approval and review are what stand in the way.
Removing this risk needs a dedicated runner group for this repository and runners registered with
`--ephemeral`, which requires access to the runner hosts.

## Before the repository goes public

CI is safe as it is only while the repository is private. Before changing its visibility:

1. **Pull requests must not reach the self-hosted runners.** Set the repository variable `CABINET_RUNNER` to `github` so every job runs on GitHub-hosted runners (free for public repositories), or change `build-and-test` so that `pull_request` runs always use `ubuntu-latest` and only pushes to `main` and tags use the self-hosted runners.
2. **Keep fork pull requests needing approval** (`all_external_contributors`), and read every diff under `.github/` before approving a run.
3. **Service containers on GitHub-hosted runners** (PostgreSQL, OpenSearch, ClickHouse) work as they are; the OpenSearch heap is sized for a 7 GB runner.
4. **Turn on private vulnerability reporting** in the repository's security settings, which SECURITY.md points to, and secret scanning with push protection.
5. **Review repository secrets and variables**: none should be needed by pull request runs.

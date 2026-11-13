# CI, releases and runner security

CI runs on the `bogware` organization's self-hosted runners. They are long-lived and shared with
other repositories, so a job that runs untrusted code could compromise more than this repository.
The workflows are written as if the repository were public already: nothing runs unless someone
with write access starts it, and every job gets only what it needs.

## Everything is manual

No workflow runs on a push, pull request, tag or schedule. Each has only a `workflow_dispatch`
trigger (CI is also called by the Release workflow), so you start every run yourself, from the
Actions tab or with `gh`:

| Workflow | What it does | Start it with |
|----------|--------------|---------------|
| `ci.yml` (CI) | Validates Compose, Kubernetes and Prometheus files, builds, runs the full test suite against PostgreSQL, OpenSearch and ClickHouse, and runs the backup drill | `gh workflow run ci.yml --ref <branch>` |
| `release.yml` (Release) | Tests a commit, builds, signs, tags and publishes a release | `gh workflow run release.yml --ref main -f version=3.0.1` |
| `soak.yml` (Soak and backup drill) | A long load run that kills instances; before a release or after changing storage, detection or delivery | `gh workflow run soak.yml --ref <branch> -f duration=1h` |

`gh run watch` follows a run you started. Run CI on a branch before merging it, since nothing does
that for you any more.

## Making a release

1. Put the release's notes in `CHANGELOG.md`. A release (`3.0.1`) needs a `## [3.0.1]` section; a
   pre-release (`3.1.0-rc.1`) takes its notes from `## [Unreleased]`. Merge to `main`.
2. Start the Release workflow from `main`:

   ```bash
   gh workflow run release.yml --ref main -f version=3.0.1
   # A pre-release
   gh workflow run release.yml --ref main -f version=3.1.0-rc.1
   # Something other than main's head: a branch, tag or full commit SHA
   gh workflow run release.yml --ref main -f version=3.0.2 -f ref=release/3.0
   ```

   `--ref main` is which copy of the workflow runs, and must be `main`, since that file decides what
   the signing key signs. `ref` is what gets released, `main` unless you say otherwise.

What it does, in order:

1. **Check inputs.** `version` must be a semantic version without a leading `v` or build metadata
   (`3.0.1`, `3.1.0-rc.1`), and `ref` a plain branch, tag or SHA. It resolves `ref` to a commit,
   refuses a tag that already exists on another commit or a release that's already published, and
   checks CHANGELOG.md has the section.
2. **Test** that commit with the whole of `ci.yml`.
3. **Build** from that same commit, stamping `version` into every program (`-p:Version`, as the tag
   used to): an archive per platform (`cabinet-<rid>.tar.gz`, `.zip` for Windows) holding the
   API, CLI and agent, `.deb` packages for the API and agent on amd64 and arm64, and the API image,
   scanned with Trivy and pushed to `ghcr.io/bogware/cabinet` as `<version>`, `sha-<commit>`, and,
   for a release but not a pre-release, `<major>.<minor>` and `latest`.
4. **Sign** every archive, package and install script (`<file>.sig`), the agent program inside
   each archive (`cabinet-agent-<rid>.sig`), and a `SHA256SUMS` of them all, then check every
   signature against `build/release-signing.pub.pem` before uploading anything.
5. **Tag** the tested commit `v<version>` through the GitHub API, create a draft release with the
   notes and files, then publish it. A version with a `-` is published as a pre-release and never
   made latest, so servers' update checks don't offer it; anything else becomes the latest release.
   Releases are immutable once published, which is why the files go to a draft first.

If a run fails, fix the cause and start it again with the same inputs. A tag it left on the same
commit is reused and a draft it left is replaced; a published release is never touched.

## The release signing key

Releases are signed with ECDSA P-256 over SHA-256. The public key is `build/release-signing.pub.pem`,
which the server and agent embed and check updates against (docs/DEPLOYMENT.md, "Verifying a
release"). The private key is the repository secret `CABINET_RELEASE_SIGNING_KEY`, a PEM
`BEGIN EC PRIVATE KEY` block. Only the Release workflow's `release` job can read it: it writes it to
a file only the runner's user can read, inside the job's own directory, refuses to go on unless it
matches the committed public key, and deletes it in a step that runs even when signing fails.

Keep an offline copy of the private key. Losing it means releases can no longer be signed with a
key the installed servers and agents trust. Changing it takes a release signed with the old key
that embeds the new one, which the workflow can't do as it stands (it requires the secret to match
the committed key), so plan a rotation before you need one.

## Which runner runs what

All jobs run on `[self-hosted, Linux, X64]`. Release jobs cross-compile the Windows and macOS builds
on Linux.

When the self-hosted runners can't be used, for example while one is out of disk, set the repository
variable `CABINET_RUNNER` to `github` (`gh variable set CABINET_RUNNER --body github`) and every job
except the soak runs on GitHub-hosted `ubuntu-latest`. Delete the variable (`gh variable delete
CABINET_RUNNER`) to go back. It applies to runs started after it changes; a re-run uses the
workflow from the original commit.

## What enforces this

- **Nothing runs by itself.** No `push`, `pull_request`, `pull_request_target`, tag or schedule
  triggers, so code nobody chose to run never reaches a runner. Only people with write access can
  start a workflow.
- **Least privilege.** The token is read-only by default, and each job asks for what it needs:
  `packages: write` for the image push, `contents: write` only for the job that tags and publishes.
  Workflows can't approve pull requests.
- **Secrets only where they're used.** The signing key is in one step's environment in one job.
  Test jobs see no secrets, and their database passwords are throwaway values for service
  containers that exist only for the job.
- **No input reaches a script unchecked.** Workflow inputs are passed to `run:` steps as environment
  variables, never written into the script, and checked against strict patterns first.
- **The release runs from `main`'s workflow file.** The release refuses to start from any other copy.
  The `release` job uses the `release` environment, so protection rules put on that environment
  (required reviewers, `main` only) apply to it.
- **Only allowed actions run**, each pinned to a full commit SHA, which the repository enforces. A
  hijacked tag can't change what CI runs, which is how `trivy-action` was compromised in 2026.

## Job isolation on shared hosts

- `.github/actions/isolate-job` gives each job its own directory for the .NET SDK, NuGet packages,
  scanner cache, registry credentials and the signing key, removed when the job ends. It's uniquely
  named because the runner's temp directory can hold leftovers from earlier jobs, and the .NET
  installer won't repair a partial install it finds there.
- `actions/checkout` runs with `persist-credentials: false`, so no token is left in `.git/config`.
- Service containers publish random host ports, so concurrent jobs can't collide.
- The scan image has a per-run tag and is removed afterwards.

## Updating actions

Updates are manual. To change an action, resolve the release tag to a commit, check it against the
project's security advisories, and pin it as `owner/action@<sha> # vX`. New third-party actions must
also be added to the repository's allowed list, including actions they call themselves. Because of
their compromise history, `trivy-action` and `setup-trivy` are allowed only at their exact commits, so
updating Trivy also means updating both entries in that list.

## Before the repository goes public

The workflows are already safe to publish: none runs on anything an outsider can do. Before changing
the repository's visibility:

1. **Protect the release.** Add protection rules to the `release` environment: `main` only as the
   deployment branch, and a required reviewer, so the signing key is used only for a release
   someone approved. Move `CABINET_RELEASE_SIGNING_KEY` from a repository secret to an
   environment secret of `release`.
2. **Enable branch protection on `main`**, which requires a public repository on the current plan:
   require pull requests, and a CODEOWNERS rule on `.github/` and `build/` so workflow and signing
   key changes always get reviewed.
3. **If pull requests should be tested automatically**, add a `pull_request` trigger (never
   `pull_request_target`) to a workflow that runs only on GitHub-hosted runners
   (`runs-on: ubuntu-latest`), has no secrets and a read-only token. Pull requests from forks must
   never reach a shared self-hosted runner. Keep fork pull requests needing approval
   (`all_external_contributors`), and read every diff under `.github/` before approving a run.
   Service containers (PostgreSQL, OpenSearch, ClickHouse) work on GitHub-hosted runners as they
   are; the OpenSearch heap is sized for a 7 GB runner.
4. **Scan the full history for secrets**, and turn on secret scanning with push protection and
   private vulnerability reporting, which SECURITY.md points to.
5. **Review repository secrets and variables**: only the release environment should hold any.

## Known limits

The runners are not ephemeral. A job that gains code execution on one could persist there and reach
later jobs from any repository sharing that runner, including a later release job while it holds the
signing key. Running only what people with write access start is what stands in the way. Removing
this risk needs a dedicated runner group for this repository and runners registered with
`--ephemeral`, which requires access to the runner hosts; until then, GitHub-hosted runners
(`CABINET_RUNNER=github`) are the safer choice for releases.

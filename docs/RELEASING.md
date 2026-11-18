# Releasing MH.DbToEntity.CLI

Releases are published to nuget.org by GitHub Actions (`.github/workflows/release.yml`) using
[Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing): no API key is stored anywhere.
`ci.yml` runs all tests (including the PostgreSQL integration tests) on every push to `master`/`develop` and on pull requests.

## One-time setup

1. **nuget.org** → your username → **Trusted Publishing** → add a policy:
   | Field | Value |
   |---|---|
   | Repository Owner | `itsmanwar` |
   | Repository | `DbToEntity` |
   | Workflow File | `release.yml` (file name only) |
   | Environment | `release` |

   If the GitHub repository is private, the policy is only *temporarily active* for 7 days, until the first successful publish.
2. **GitHub** → repository **Settings → Environments** → create `release`. Add yourself under **Required reviewers**, so every publish waits for your approval.
3. **GitHub** → **Settings → Secrets and variables → Actions** → add secret **`NUGET_USER`** = your nuget.org **profile name** (not your email).

## Each release

1. Update `<Version>` and `<PackageReleaseNotes>` in `DbToEntity.CLI/DbToEntity.CLI.csproj` (and `<Version>` in `DbToEntity.Core/DbToEntity.Core.csproj`), and the README if behaviour changed.
2. Merge to `master`.
3. Tag the merge commit with the same version and push the tag:
   ```powershell
   git tag v2.0.0
   git push origin v2.0.0
   ```
4. In GitHub **Actions → Release**, approve the `release` environment when asked. The workflow:
   - runs all tests,
   - fails if the tag doesn't match `<Version>` (nuget.org versions can't be deleted, only unlisted),
   - packs, gets a temporary 1-hour API key via `NuGet/login`, and pushes.

Never re-publish a changed build under the same version: bump the version (e.g. `2.0.1`) instead.

## Troubleshooting

- **403 from `NuGet/login`**: the job is missing `permissions: id-token: write`, or the policy fields don't match (owner, repo, workflow file name, environment).
- **"Tag … does not match `<Version>`"**: fix the tag or the `.csproj`, delete the wrong tag (`git push origin :refs/tags/vX.Y.Z`), and tag again.

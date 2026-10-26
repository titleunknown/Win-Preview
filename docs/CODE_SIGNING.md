# Code signing with SignPath Foundation

Windows shows a "Windows protected your PC" warning for unsigned downloads. SignPath Foundation signs open-source
projects for free. This page covers what the repository already contains, what you need to do on SignPath's side,
and how releases work once it's connected.

**Current status:** not applied yet. Releases are built and published unsigned (see "Release process").

## What's already in the repository

| File | Purpose |
|---|---|
| `.github/workflows/release.yml` | On a `v*` tag, builds on a GitHub-hosted runner, signs through SignPath **if configured**, and creates a *draft* GitHub release. Without SignPath variables it simply builds an unsigned package. |
| `.github/workflows/ci.yml` | Builds and runs the tests on every push and pull request. |
| `.signpath/artifact-configuration.xml` | Tells SignPath which files to sign: `WinPreview.exe`, `WinPreview.dll`, `WinPreview.Core.dll`. Third-party DLLs are never signed under our certificate. |
| `Directory.Build.props` | One product name (`Win-Preview`) and one version for every binary, which SignPath requires. |

## SignPath Foundation's conditions, and how Win-Preview stands

From <https://signpath.org/terms>:

| Condition | Status |
|---|---|
| OSI-approved license, no commercial dual licensing | ✅ GPL-3.0-or-later |
| Actively maintained, already released in the form to be signed | ✅ v0.1 exists. Apply after the next release. |
| Own binaries only; upstream libraries stay unsigned or keep their publisher's signature | ✅ only our three assemblies are in the artifact configuration |
| Built verifiably from source on GitHub-hosted agents | ✅ `release.yml` (not yet run on GitHub; run it once in test mode first, see below) |
| Multi-factor authentication for everyone with SignPath or repo access | ⚠️ **You:** turn on 2FA for GitHub and SignPath |
| Roles: Authors, Reviewers, Approvers | ⚠️ Listed in the README policy below. For a one-person project all three are the same person. |
| Privacy: no network transfer unless the user asks | ✅ see the README's Privacy section |
| "Code signing policy" page and attribution wording | ⏳ Add after approval (text below). Don't add it before, because it would be untrue. |
| **No proprietary components** | ⚠️ **Open question, see next section** |

### The one real risk: Microsoft components in the package

The terms say a signed project may not include proprietary components, with an exception for GPL "system libraries".
Win-Preview's self-contained package bundles Microsoft's Windows App SDK runtime (licensed under Microsoft's own
terms, not an OSI license), plus the Microsoft Visual C++ runtime DLLs shipped with qpdf. They are Microsoft-signed redistributables, not our code, but a strict reading could count them.

SignPath decides each application by hand. Options if they object:

1. **Explain it** in the application: the components are unmodified Microsoft redistributables, listed in
   `THIRD-PARTY-NOTICES.md`, signed by Microsoft, and not signed by us.
2. **Switch to framework-dependent**: the zip then holds only open-source code, and users install Microsoft's free
   [Windows App Runtime](https://learn.microsoft.com/windows/apps/windows-app-sdk/downloads) once (it ships with
   some Windows builds). This makes the first run less convenient.

Ask for their view in the application form so you know before investing in the setup.

## Steps for you (SignPath's side)

1. **Turn on two-factor authentication** on your GitHub account.
2. **Apply** at <https://signpath.org/apply> (SignPath Foundation, "Open source projects"). Suggested answers:
   - Project name: **Win-Preview**
   - Repository / homepage: <https://github.com/titleunknown/win-preview>
   - Download page: <https://github.com/titleunknown/win-preview/releases>
   - License: GPL-3.0-or-later
   - Description: a free Windows viewer and markup tool for PDFs and images, inspired by macOS Preview
   - What is signed: our own .NET assemblies and the app executable; no third-party binaries
   - Mention the Microsoft redistributables question above.
3. **After approval**, in the SignPath web app:
   1. Create the **project** (slug suggestion: `win-preview`) and upload `.signpath/artifact-configuration.xml` as the
      artifact configuration with slug `initial`.
   2. Add the predefined **GitHub.com trusted build system** to your organization and link it to the project.
   3. Use the `release-signing` signing policy (and `test-signing` for trial runs). Make yourself its approver.
   4. Create an **API token** for a user with *submitter* permission on that policy.
4. **In the GitHub repository** (Settings → Secrets and variables → Actions):
   - Secret `SIGNPATH_API_TOKEN` = the API token
   - Variables `SIGNPATH_ORGANIZATION_ID` and `SIGNPATH_PROJECT_SLUG`
   - Optional variables `SIGNPATH_SIGNING_POLICY_SLUG` (default `release-signing`) and
     `SIGNPATH_ARTIFACT_CONFIGURATION_SLUG` (default `initial`)
5. **Trial run:** Actions → Release → *Run workflow*. It builds and uploads a test package as a workflow artifact
   without creating a release. With `test-signing` this proves the whole chain works.
6. **Add the policy text** below to the README and to the release page template.

## Release process

```bash
git tag v0.3
git push origin v0.3
```

1. GitHub Actions builds, tests and publishes the app.
2. If SignPath is configured, the build is submitted for signing; **you approve the request** in SignPath (every
   release needs a manual approval).
3. The workflow verifies the signatures, packages `Win-Preview-v0.3-win-x64.zip` plus a `.sha256` file and creates a
   **draft** release. Review the notes and click Publish.

Without SignPath variables the same workflow produces an unsigned package.

## Text to add to the README after approval

> ## Code signing policy
>
> Free code signing provided by [SignPath.io](https://signpath.io), certificate by
> [SignPath Foundation](https://signpath.org).
>
> - **Authors** (trusted committers): [@titleunknown](https://github.com/titleunknown)
> - **Reviewers** (review changes from non-committers): [@titleunknown](https://github.com/titleunknown)
> - **Approvers** (approve each signing request): [@titleunknown](https://github.com/titleunknown)
>
> Only binaries built from this repository by GitHub Actions are signed: `WinPreview.exe`, `WinPreview.dll` and
> `WinPreview.Core.dll`. Third-party components keep their own publishers' signatures.
> See the [Privacy policy](#privacy).

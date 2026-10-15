# Publishing Scour

Releases are automatic: bump `<Version>` in `src/Scour.App/Scour.App.csproj`, update `docs/release-notes.md` and push to
`main`. CI builds `Scour.exe`, tags `v<version>`, publishes the GitHub Release (which installed copies update from) and
writes winget manifests.

Two optional steps switch on when you add repository secrets (**Settings → Secrets and variables → Actions → New
repository secret**). Until then they're skipped and everything else works as before.

## Code signing (removes the SmartScreen "unknown publisher" warning)

Pick **one** of these.

### Option A — Azure Trusted Signing (about $10/month, recommended)

1. In the Azure portal create a *Trusted Signing account* and complete *identity validation* (individual or organisation).
2. Create a *certificate profile* (Public Trust).
3. Create an app registration (Microsoft Entra ID → App registrations), add a client secret, and give it the
   **Trusted Signing Certificate Profile Signer** role on the signing account.
4. Add these secrets:

| Secret | Value |
| --- | --- |
| `AZURE_TENANT_ID` | Directory (tenant) ID of the app registration |
| `AZURE_CLIENT_ID` | Application (client) ID |
| `AZURE_CLIENT_SECRET` | The client secret's value |
| `AZURE_SIGNING_ENDPOINT` | Your account's endpoint, e.g. `https://eus.codesigning.azure.net` |
| `AZURE_SIGNING_ACCOUNT` | Trusted Signing account name |
| `AZURE_SIGNING_PROFILE` | Certificate profile name |

### Option B — a code-signing certificate file (.pfx)

| Secret | Value |
| --- | --- |
| `SIGN_PFX_BASE64` | The .pfx file as base64 (`[Convert]::ToBase64String([IO.File]::ReadAllBytes('cert.pfx'))`) |
| `SIGN_PFX_PASSWORD` | The .pfx password |

Open-source projects can also apply to [SignPath Foundation](https://signpath.org) for free signing; that needs an
OSI-approved licence in the repository.

The **Sign Scour.exe** step in the build log shows the signature status afterwards.

## winget (`winget install hqjake.Scour`)

Every release run uploads a **winget-manifests** artifact with the three manifest files.

- **First time:** a package has to be submitted once to [microsoft/winget-pkgs](https://github.com/microsoft/winget-pkgs).
  Either add the `WINGET_TOKEN` secret below before the next release, or download the artifact and run
  `wingetcreate submit --token <token> <folder>` yourself.
- **Every release after that:** with `WINGET_TOKEN` set, CI opens the pull request to winget-pkgs automatically.
  Microsoft's bots validate it and a moderator merges it, usually within a day or two.

`WINGET_TOKEN` is a GitHub personal access token (classic) with the `public_repo` scope, from the account that should
own the winget-pkgs pull requests.

The manifests say `License: Proprietary` because the repository has no licence file yet. Add a `LICENSE` (MIT, for
example) and change the line in `packaging/winget/New-WingetManifest.ps1` to match.

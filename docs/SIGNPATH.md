# SignPath onboarding and signed releases

## Status and prerequisites

The integration is implemented but **not yet activated**. SignPath acceptance, account MFA, a production policy/certificate and service-side configuration are external prerequisites. Existing releases remain unsigned. Application entry point: [SignPath Foundation](https://signpath.org/apply.html). Their [terms](https://signpath.org/terms) govern acceptance; there is no guaranteed review time or acceptance.

The MIT license, release downloads, illustrated functionality, privacy/security policies and signing policy are in this repository. Before making the source public, scan the complete reachable history, inspect documentation/screenshots and check release packages. Never publish local app data or the contact email/application stored in the ignored build folder. Public visibility exposes history and screenshots; removing a file later does not retract public copies.

## Application

Use the public repository URL, Releases URL, MIT license and [code signing policy](../CODE_SIGNING_POLICY.md). Describe AIPULSE as local hardware/model monitoring with optional administrator-installed boot services, a user-configured HTTPS hub, allowlisted token metadata and no remote execution protocol. Ask the reviewer to approve three artifact configurations: `app`, `uninstaller` and `installer`, using the XML files under `signpath/`. The generated Inno uninstaller and product/version matching must be reviewed explicitly. Do not state that sponsorship has been granted before acceptance.

The maintainer submits the application with their monitored contact email. Keep that address and later API tokens out of committed files. Enable MFA on GitHub and SignPath. Install the official SignPath GitHub App for this repository when requested during onboarding; the repository-specific grant and account consent belong to the account owner.

## Configuration after acceptance

1. In SignPath, link the AIPULSE project to the predefined GitHub.com trusted build system. Restrict the repository, `main` branch and `.github/workflows/build.yml`; require GitHub-hosted build agents. Import the reviewed `app`, `uninstaller` and `installer` XML configurations.
2. Create a production policy with the maintainer as a **manual approver**, plus a submitter-only token. Keep production approval independent of possession of the token. Ensure timestamping is enabled.
3. In GitHub, create a protected environment called `signpath-signing`, restricted to `main`, requiring the maintainer's review. Put `SIGNPATH_API_TOKEN` in this environment's secrets, not a repository-wide secret. Ordinary builds use `unsigned-build` and have no signing token.
4. Set environment/repository variables `SIGNPATH_ORGANIZATION_ID`, `SIGNPATH_PROJECT_SLUG`, `SIGNPATH_POLICY_SLUG`, and `SIGNPATH_CERTIFICATE_THUMBPRINT`. The last value is the approved certificate's 40-hex SHA-1 thumbprint **identifier**, not the signing hash algorithm. The file signatures use SHA-256. The certificate thumbprint must also be a repository variable for publication verification.
5. Set `SIGNPATH_ENABLED=true` only after these controls are in place. Never paste the API token or private key into chat, source, workflow inputs or logs. SignPath retains the private key.

## Build and publish

Choose a new version rather than replacing published unsigned 0.3.0 files. Update the app, installer and platform packaging version references together. Run **Build and validate → Run workflow → main → sign_windows=true**. The workflow fails before requesting signatures if required values are missing.

The pipeline tests the app, requests signatures for its EXE/DLL, captures and signs the generated Inno uninstaller, compiles the installer with verified signed contents, then signs the final setup EXE. Each request waits up to an hour for approval. Review the repository, exact commit, product/version, files and passing checks in SignPath before approving each request. A timeout/rejection stops the build. No local or unsigned fallback is published.

Returned files must have a valid Authenticode chain, the configured signer, a timestamp and matching product metadata. A separate PE comparison rejects changes to executable payload; only the checksum, security directory, alignment padding and certificate data may differ. Third-party files are never submitted for re-signing.

The portable ZIP is created after application signing. `SIGNING-VERIFICATION.json` records signed file hashes, signer/timestamp identity, final Windows package hashes, repository, commit and run ID. Checksums are generated last. The Windows install/uninstall test runs against the signed result.

After the complete Windows/Ubuntu build succeeds, create a draft targeting that exact full commit. Run **Publish verified release artifacts** with its run ID, tag and `require_signed_windows=true`. Publication checks the verification receipt and all uploaded asset digests. An explicitly unsigned preview requires setting this flag to false and labeling the release accordingly. Existing published releases cannot be modified by this workflow.

## Limits and recovery

The integration cannot issue a trusted signature before SignPath accepts and configures the project. Its schema/pipeline settings must be validated in the actual SignPath account; local tests are not evidence of a production signature. Certificate renewal needs a reviewed thumbprint update. Missing/upstream unsigned DLLs can still trigger a device policy, so a signed release needs a final install test on the intended Windows PC.

GitHub Actions artifact storage is required by SignPath's trusted build connector. The private repository previously reached its storage quota; the new signing inputs retain three days and final artifacts seven days. Existing retained artifacts are not automatically deleted. Resolve quota through intentional retention/cleanup or the applicable public-repository allowance before a signing run. Do not increase billing automatically.

Official references: [GitHub integration](https://docs.signpath.io/trusted-build-systems/github), [artifact syntax](https://docs.signpath.io/artifact-configuration/syntax), [reference and metadata restrictions](https://docs.signpath.io/artifact-configuration/reference), [Inno signing](https://jrsoftware.org/ishelp/topic_setup_signtool.htm).

# Code signing policy

## Current status

AIPULSE is preparing an application to SignPath Foundation. Acceptance, a production certificate and a completed signed release have **not** been granted or verified. Existing 0.3.0 downloads remain unsigned. Adding this policy or a signing workflow does not change their trust status.

After acceptance, the intended attribution is: **Free code signing provided by [SignPath.io](https://signpath.io), certificate by [SignPath Foundation](https://signpath.org).** This describes the proposed sponsorship and is not a claim that SignPath currently signs or endorses AIPULSE.

## People and approval

- Committer and reviewer: [nikhilgs817](https://github.com/nikhilgs817).
- Release/signing approver: [nikhilgs817](https://github.com/nikhilgs817).
- Participants must enable multi-factor authentication on GitHub and SignPath before production signing. Account MFA has not been independently verified by this repository.
- Every production signing request requires manual approval in SignPath. A submitter API token alone must not authorize production signatures.

Only reviewed code on this repository's `main` branch can enter production signing. Pull requests and ordinary pushes do not request signatures. The maintainer explicitly runs the build with `sign_windows` enabled, through a protected `signpath-signing` environment, after the project has been accepted and configured. All preceding jobs run on GitHub-hosted runners.

## What may be signed

The allowlist is AIPULSE's `AiPulse.exe` and `AiPulse.dll`, its generated Inno Setup uninstaller and its final setup EXE. These are built from this repository's source and installer script. The project/version metadata is restricted by the role-specific configurations under [signpath/](signpath/). SignPath must review and approve the generated Inno uninstaller configuration as part of onboarding.

Upstream .NET, Avalonia, SQLite, hardware libraries, drivers, optional model weights and third-party runtime tools are **not** re-signed with this certificate. Their original signatures and licenses are retained. The installer can include upstream files under their own licenses. Kernel drivers are not built or signed by this project.

## Build and verification

The pipeline signs the owned application files first, then the generated uninstaller, then the final installer. It verifies the returned Authenticode signature, configured certificate thumbprint, timestamp, product/version metadata and unchanged executable payload at each stage. Missing configuration or failed verification stops the signed build; there is no unsigned fallback for a signed request.

The portable ZIP is assembled after application signing. SHA-256 manifests are written after the completed files. Signed releases include a verification receipt tying the package hashes to the exact repository, build run, commit and certificate identity. Publication requires a successful trusted build, matching release source and matching uploaded hashes. Already published unsigned assets are not silently replaced or relabelled as signed.

## Privacy and system changes

See the [privacy policy](PRIVACY.md), [sensor setup](docs/INSTALLATION.md#deep-sensors-on-windows) and [central service guide](docs/CENTRAL-MONITORING.md). Users explicitly configure remote destinations and authorize service/driver installation. Services have documented removal steps and preserve user data. No remote command execution is part of the fleet telemetry protocol.

For signing-process concerns, contact the maintainer through the [security reporting instructions](SECURITY.md). An Authenticode signature identifies a build's publisher and integrity; it does not guarantee compatibility with every device policy.

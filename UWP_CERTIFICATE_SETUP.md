# UWP Certificate Setup (Local Dev)

This project references `Assets/WSATestCertificate.pfx` in `ProjectSettings/ProjectSettings.asset`.

## Why this matters

- Unity UWP exports can fail signing/deployment on a clean machine if the certificate file is missing.
- `.pfx` files are ignored by `.gitignore` on purpose and should not be committed.

## Local setup steps

1. In Unity, switch build target to **Universal Windows Platform**.
2. Open **Player Settings > Publishing Settings**.
3. Under **Package Certificate**, create/import a local test certificate:
   - Use Unity's **Create...** option for local debug builds, or
   - Import your team-provided `.pfx` from a secure channel.
4. Save it as `Assets/WSATestCertificate.pfx` (or update `metroCertificatePath` to your chosen path).
5. Re-export UWP and build in Visual Studio (ARM64, Release, Device).

## Security rules

- Never commit `.pfx` files or certificate passwords.
- Use separate signing identities for development and production releases.

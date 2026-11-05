# Security policy

## Reporting a vulnerability

Please do not open a public issue for a security problem. Use GitHub's private vulnerability reporting instead: on the repository page choose **Security** → **Report a vulnerability**.

Include what you found, how to reproduce it, and the version or commit you tested. You can expect a first answer within a week. Inkpact is maintained by one person, so fixes are made as soon as time allows, and you will be credited in the release notes unless you prefer not to be.

## Supported versions

Inkpact has no stable release yet. Until 1.0, only the latest commit on `main` is supported.

## Scope

In scope: the server, the web client and the Docker image in this repository. Out of scope: problems in your own deployment (reverse proxy, TLS, host hardening) and vulnerabilities in third-party dependencies that are not reachable through Inkpact.

## What is scanned

Every pull request, every push to main and every day: dependencies for known vulnerabilities (`pnpm audit` and the NuGet vulnerability list), new dependencies (GitHub dependency review) and the licences of all dependencies. CodeQL analyses the C# and TypeScript code and the workflows, and Trivy scans the container image and the Dockerfile; the published `edge` image is rescanned weekly. Dependabot proposes dependency updates and security updates. Findings of CodeQL and Trivy are on the repository's **Security** tab.

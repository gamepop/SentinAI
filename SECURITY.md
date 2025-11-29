# Security Policy

## Supported Versions

Use this section to tell people about which versions of your project are currently being supported with security updates.

| Version | Supported          |
| ------- | ------------------ |
| 1.0.x   | :white_check_mark: |
| < 1.0   | :x:                |

## Reporting a Vulnerability

We take the security of SentinAI seriously. If you discover a security vulnerability, please follow these steps:

1.  **Do not open a public issue.**
2.  Email the security team at [security@sentinai.com](mailto:security@sentinai.com) (or the maintainer's email if no dedicated security email exists).
3.  Include a detailed description of the vulnerability, steps to reproduce, and potential impact.
4.  We will acknowledge your report within 48 hours.
5.  We will work with you to verify and fix the issue.
6.  Once fixed, we will release a security update and credit you for the discovery (if desired).

### Local-First Security Model

SentinAI is designed with a "Local-First" security model.
- **No Telemetry:** We do not collect usage data or file paths.
- **Local Inference:** All AI models run locally on your machine.
- **No Cloud Uploads:** Your files are never uploaded to any cloud service.

If you find a way that data is inadvertently leaving the local machine, please report it immediately as a critical vulnerability.

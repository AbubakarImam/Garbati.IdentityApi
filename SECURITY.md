# Security Policy

GARBATI is a core-banking platform. A real vulnerability here could mean a forged authorization grant, a replayed or duplicated transaction, or exposure of a customer's identity data. We take reports seriously and ask that you report privately, before anything is made public.

This policy applies to every repository in the GARBATI platform: `garbati-platform`, `LedgerApi`, `Garbati.IdentityApi`, `Garbati.OrchestratorApi`, `Garbati.StaffIdentityApi`, `Garbati.OpsApi`, and `garbati-ops-console`.

## Reporting a vulnerability

**Email `salamabubakarimam@gmail.com` directly. Do not open a public GitHub issue for a security report.** A public issue discloses the problem before anyone has had a chance to fix it.

Please include:

- Which repository and, if you know it, which file/endpoint/decision in `docs/DESIGN.md` is affected.
- A clear description of the vulnerability and its potential impact (what an attacker could actually do — forge a grant, duplicate a transaction, read another customer's data, etc.).
- Steps to reproduce, or a proof-of-concept if you have one.
- Whether you tested this against a locally-run instance or against a live GARBATI demo deployment.

## What to expect

GARBATI is currently maintained by a single person, without a dedicated security team, and this policy makes **no promise of a specific response time**. What you can expect:

- Your report will be read and taken seriously — this is core-banking software, and a real finding is treated as a real finding regardless of how small the project is today.
- We will not disclose your report, or credit you publicly, without your permission.
- Once a reported vulnerability is confirmed and fixed, we intend to follow a private-first, public-after-fix disclosure process (the same model projects like Apache Fineract use) — reported privately, fixed, then disclosed, not the other way around.

## Scope

- **In scope:** any code in the repositories listed above, including GARBATI's deployed demo environment on Azure (the same instances linked from each service's own `README.md`).
- **Out of scope:** denial-of-service testing against the live demo environment, social engineering, and physical security. If you're not sure whether something is in scope, email first and ask — we would rather clarify than have you hold back a real finding.
- The demo environment holds no real customer data — it exists for testing and demonstration only.

## Compliance posture

GARBATI makes **no claim** of PCI-DSS, SOC 2, or ISO 27001 certification or compliance. Achieving and maintaining any such certification is the responsibility of the institution deploying GARBATI for real use, not something this project provides out of the box. This is the same posture Apache Fineract takes — a de facto industry norm for open-source core-banking software, not a GARBATI-specific limitation.

## Supported versions

GARBATI does not yet have versioned releases — only a `main` branch per repository. Report against `main`; there is nothing older to patch separately yet.

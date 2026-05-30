# Security Policy

## Reporting

Do not file public issues for security bugs.

Report privately via [GitHub Security Advisories](https://github.com/trevorftp/Stratum/security/advisories/new).

Include:

* A description of the issue and its impact.
* Steps to reproduce or a proof of concept.
* Stratum version (`StratumServer --stratum-version`) and the vanilla version
  it was tested against.
* Any suggested mitigation.

Expect acknowledgement within a few days. Fix and advisory target: 30 days
for high severity, 90 days for low. Vanilla Vintage Story bugs get forwarded
to Anego Studios.

## Scope

In scope:

* RCE, privilege escalation, or auth bypass in the Stratum server or launcher.
* Crashes or resource exhaustion triggerable by an unauthenticated client.
* Data corruption or world tampering through normal network traffic.
* Information disclosure from the server process or save files.

Out of scope:

* Vanilla bugs Stratum does not amplify.
* Third-party mods.
* Issues requiring shell access to the host.
* DoS via authenticated staff accounts.

## Supported versions

Only the latest release on `main` gets security fixes.

## Safe harbor

No legal action against researchers who:

* Avoid privacy violations and service disruption.
* Only test systems they own or have permission to test.
* Give reasonable time to mitigate before public disclosure.

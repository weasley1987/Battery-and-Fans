# SignPath application draft

Project: Battery and Fans
Repository: https://github.com/weasley1987/Battery-and-Fans
Maintainer and signing approver: weasley1987
License: MIT
Language/platform: C#, .NET Framework, Windows x64
Version: 1.6.0.0

Battery and Fans is a small desktop tray utility displaying battery percentage, charge/discharge power, battery wear and estimated remaining or charging time alongside CPU, RAM and GPU utilization. It uses Windows APIs and contains no bundled third-party drivers. It stores settings locally and has no application network telemetry.

The maintainer intends to build release binaries from the public repository with GitHub Actions, require manual approval for production signing and comply with SignPath Foundation policies. Code signing policy is at CODE_SIGNING.md.

This is a newly public project with limited public reputation. Earlier personal builds were unsigned and Smart App Control blocked one of them because its publisher could not be verified. Sponsorship and acceptance are subject to SignPath review.

Before submitting:
- Review the published source and MIT license.
- Enable MFA on GitHub and SignPath.
- Publish an unsigned GitHub Release built from a successful CI run, with version, source commit, SHA256 and a link to CODE_SIGNING.md.
- Provide the owner's contact details in the official application form.
- Submit at https://signpath.org/apply and disclose the project's early stage.
- After approval, configure the assigned SignPath project and artifact policy, then add the official signing integration with manual release approval.

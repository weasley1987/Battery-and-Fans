# Code signing policy

## Current status

Builds are unsigned. SignPath Foundation sponsorship is planned, with approval pending. The project does not currently receive signing services from SignPath.

## Roles

Author, maintainer, reviewer and intended release signing approver: [weasley1987](https://github.com/weasley1987). Changes from contributors require maintainer review. Before activating signing, the maintainer must enable MFA for GitHub and SignPath.

## Release approval

Only artifacts built by CI from reviewed source in this repository may be submitted for signing. Each production signing request must receive manual approval from the maintainer. Signing secrets must be stored in CI secrets and excluded from source control.

## Privacy policy

This program will not transfer any information to other networked systems unless specifically requested by the user or the person installing or operating it.

## Attribution after approval

If sponsorship is approved and activated, the release page and this policy will include the required attribution to SignPath.io and SignPath Foundation. Unsigned releases must remain clearly labeled.

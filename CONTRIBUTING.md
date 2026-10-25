# Contributing to the Antiokh fork

This document applies to contributions submitted to **Antiokh/google-calendar-desktop-widget**. It does not change the licensing or policies of [the original project](https://github.com/mkazemie/google-calendar-desktop-widget), and it is not a contribution agreement with the original project's maintainer.

## License of contributions — AGPL only

This fork contains code licensed under **GNU AGPL-3.0-or-later**. Contributions made to this fork must be compatible with that license and, if accepted into the covered program, will be distributed under **AGPL-3.0-or-later**.

By submitting a contribution, you confirm that you have the rights required to submit it and license it as described above, including rights relating to any third-party code you introduce.

**We do not require copyright assignment, proprietary relicensing rights, commercial sublicensing grants, or a contributor license agreement beyond the rights granted by AGPL.** Copyright in original contributions remains with its respective copyright holders, subject to the public license.

AI-assisted tools are permitted in the development process. Contributors remain responsible for reviewing their submissions, checking licensing compatibility, and ensuring they have rights to submit any included material. Authorship and copyrightability depend on applicable law and the actual human contribution.

## How to contribute

- Prefer discussing non-trivial changes before implementation (via Issues, when enabled, or a draft pull request).
- Keep changes focused, with a clearly described feature or bug, a test plan, and compatible licensing.
- Preserve existing copyright and attribution notices, and identify substantial new contributions appropriately.
- Ensure `dotnet build CalendarWidget/CalendarWidget.csproj -c Release` succeeds on Windows.
- Test Windows Explorer, Show Desktop, DPI and multi-monitor behaviors on a real Windows device where applicable. Build success alone is not proof of correct desktop-shell behavior.

## Commercial use

The AGPL permits commercial activity, including selling software copies, integrations, support, deployment and hosting, subject to the license's requirements. Recipients retain the applicable freedoms to inspect, modify and redistribute the covered software. **Commercial** and **proprietary** are different concepts.

For closed-source licensing of code within this fork, permissions would be required from **all relevant copyright holders**; the fork maintainer cannot unilaterally grant such permissions for third-party code.

This is an independently maintained fork, not a change to the original maintainer's contribution terms. See [GNU AGPL-3.0](https://www.gnu.org/licenses/agpl-3.0.en.html).

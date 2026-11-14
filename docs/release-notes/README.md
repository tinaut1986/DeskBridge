# Release notes

One file per tag and interface language, written on a topic branch and merged into the release
branch **before** the tag exists (same scheme as llm-browser):

- `vX.Y.Z.md`: English. The CI copies it into the GitHub Release body between
  `<!-- deskbridge-notes -->` markers; the updater shows that block when there is no file for the
  user's language.
- `vX.Y.Z.<code>.md`, **one for every other interface language**: `es`, `ca`, `fr`, `de`, `it`,
  `pt`, `ru`, `ja`, `zh`, `tw` (the codes in `src/DeskBridge.Core/Localization/L.cs`, the same as
  NSA and llm-browser; a new language there needs a file for every tag too). Same lines as the
  English, in the same order. Menu and setting names as the app shows them in that language
  (`src/DeskBridge.Core/Locales/<code>.json`); shortcuts, product names and paths unchanged.
  Portuguese is Brazilian; `zh` is Simplified, `tw` Traditional (Taiwan).

The updater window reads the translation from `main` on GitHub
(`raw.githubusercontent.com/.../main/docs/release-notes/<tag>.<code>.md`), so a translation fixed
after the release reaches users without a new build; a beta not merged into `main` yet is read from
its own tag. The release body keeps only English.

- 3 to 6 short lines, each starting with `- `, plain text for the user.
- A **stable** release lists everything since the previous *stable* tag, betas included; a **beta**
  lists only what is new since the previous tag.

To see the updater window with some notes without publishing anything, run a local build with
`DESKBRIDGE_UPDATER_DEMO` set to a folder holding `vX.Y.Z.md` / `vX.Y.Z.<code>.md` files with a
version above the one in `Directory.Build.props`: it simulates downloading the highest one.

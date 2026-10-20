# DisplaySpaces icon

Two separate stacks of workspace cards represent independent virtual desktops on
multiple displays. Cyan matches the overlay accent; violet distinguishes the
second workspace. The navy tile has transparent outer corners and no text, so
the same mark works in every supported UI language.

- `DisplaySpaces.png`: original artwork generated with ImageGen.
- `DisplaySpaces.ico`: 32-bit alpha icon containing 16, 20, 24, 32, 40, 48,
  64, 96, 128 and 256 pixel frames.

Regenerate the ICO from the PNG on Windows with `scripts/convert-app-icon.ps1`.
The project embeds the ICO in both the executable's Windows resources and the
managed assembly. Runtime icon loading does not depend on loose asset files.

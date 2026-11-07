# Translations

Every file here is one language. SysWidget picks the one matching the Windows display language
(or the one chosen in **Settings → Appearance → Language**) and falls back to English for anything missing.

## Fixing or adding a translation

1. Open the language's file, or copy [`en.txt`](en.txt) to a new file named after the language code:
   `de.txt`, `uk.txt`, `pt-BR.txt`, `zh-Hans.txt`... (the code Windows uses for that language).
2. Translate the text after each `=`. Keep the key before it as it is.
3. Open a pull request. A check runs on it automatically and points out any problems.

Once the pull request is merged, everyone using that language gets the new text on SysWidget's next
update check (while **Check for updates** is on). No new release is needed.

## Format

```
# a comment
menu.exit = Exit
ram.value = {0} / {1} GB
```

- `{0}`, `{1}`... are filled in by the program (numbers, names). Keep them; you can move them around.
- `\n` is a line break.
- Units are part of the text (`GB`, `KB/s`, `W`), so use the forms your language uses (`ГБ`, `Ko/s`...).
- Short labels such as `label.cpu` or `label.net` appear in the widget itself, so keep them short.
  Task Manager's wording in your language is a good guide.
- Any line you leave out shows in English.

## Trying it before sending

Put your file in a `lang` folder next to `SysWidget.exe`, then pick the language in
**Settings → Appearance → Language** (or restart SysWidget). A file there overrides the built-in one.

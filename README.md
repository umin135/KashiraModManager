# Kashira Mod Manager
![screenshot_20171221-151714](https://raw.githubusercontent.com/umin135/KashiraModManager/refs/heads/main/readme_res/title.png)
## Introduction
Kashira Mod Manager is a universal mod manager for patching mods into games built on Koei Tecmo's **Katana Engine**.

The project is being tested and developed primarily on DOA6LR (Dead or Alive 6: Last Round).
Currently only DebugMod is supported. Support for a single mod-file format (`.ktmod`) is planned.
If you need help or would like to contribute to our research, please join our [Discord Server](https://discord.gg/jwaB8zhb9v).

## How to Use
1. Download `Kashira.exe` and place it wherever you like. (Any location is fine, but keep it somewhere safe so you don't delete it by accident.)
2. Launch it and click **Scan for games** to detect Katana Engine games installed through Steam and add them to the list. You can also add a game manually with **New**.
   (For now only a small number of games will be detected. More support will be added later.)
3. **Select** a game to open the main screen; a mod folder will be created inside the actual game directory.
4. On the main screen, click **Open folder** to open the directory where mod files go. Drop mod files (`.ktmod`) or raw files to patch there, and the manager will detect them.
5. Click **Apply** to apply the mods to the game. This is safe because it never modifies the large original `.fdata` files directly — only the comparatively small `.rdb` index is changed, so reverting is fast and simple.

## Notes
- The single-file mod format `.ktmod` is still in development; we're working to make it convenient for both modders and users.
- Development is currently based on DOA6LR. We're working toward supporting more games in the future.

## Credits
Thanks to everyone who analyzed the Katana Engine RDB format and made this possible.
This project builds on the reverse-engineering work of the projects below.

### RDB / RDX / FDATA
- eterniti ([rdbtool](https://github.com/eterniti/rdbtool) | [qrdbtool](https://github.com/eterniti/qrdbtool) | [redelbe](https://github.com/eterniti/redelbe))
- eArmada8 ([yumia_fdata_tools](https://github.com/eArmada8/yumia_fdata_tools))
- DeathChaos25 ([fdata_dump](https://github.com/DeathChaos25/fdata_dump))
- MrIkso ([RDBExplorer](https://github.com/MrIkso/RDBExplorer))
- MangetsuC ([yumia_easy_mod_manager](https://github.com/MangetsuC/yumia_easy_mod_manager))
- shell-man-5 ([Nioh3-Mod-Manager](https://github.com/shell-man-5/Nioh3-Mod-Manager))
- bnnm ([vgm-tools](https://github.com/bnnm/vgm-tools) — koei rdb+fdata BMS script)

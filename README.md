# Arr Companion

An Emby plugin that adds the titles Home Screen Companion finds missing from the library to Sonarr and Radarr: by hand from an admin page, or with rules such as "the top 3 of Trending".

## How it works

- **The list.** Home Screen Companion's Missing tab, with "Keep the list in a file" and JSON on, writes `missing_titles.json` after each sync and after library changes. Arr Companion reads that file. It finds Home Screen Companion's export folder by itself; a different file or folder can be set under Settings. For each title the file gives the lists it is on and its place on each one (`ranks: [{ list, rank, of }]`).
- **Missing tab.** Every title not in the library, with its places ("#1 Trending /20"), and whether Sonarr or Radarr already has it. Filter by list, type, top places and status. Add one title or a selection (root folder, quality profile, monitoring, search), or ignore titles.
- **Rules.** A rule names a list (or any list), movies, shows or both, the top places to take, and optionally a first year and a limit per run. Rules run on the scheduled task *Arr Companion › Run rules* (every 6 hours) and, if that is on, a few minutes after the file changes. They use the server's default root folder and profile. A title a rule has added once is never added again, so one removed from Sonarr or Radarr stays removed.
- **History.** What was added, by hand or by which rule, and from which place on which list.

Shows already in the library, with missing episodes, are left out for now: version 1 adds whole series.

## Servers

Settings has one Sonarr and one Radarr. They are saved as a list (`Instances`, each with its own id), and adds and rules pick a server by id, so more than one of each kind only needs the page. Emby's server talks to Sonarr and Radarr. The API keys never reach the browser.

## Requirements
- HomeScreenCompanion v4.5.1.0 and above
https://github.com/soderlund91/HomeScreenCompanion
- Sonarr and Radarr with API v3.

## Install

Download the .dll file from latest releases. Place the .dll in you plugin folder. Restart Emby. 

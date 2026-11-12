# Arr Companion

<p align="center"><img src="ArrCompanion/thumb.png" alt="Arr Companion" width="480"></p>

An Emby plugin that adds the titles Home Screen Companion finds missing from the library to Sonarr and Radarr: by hand from an admin page, or with rules such as "the top 3 of Trending".

## How it works

- **The list.** Home Screen Companion's Missing tab, with "Keep the list in a file" and JSON on, writes `missing_titles.json` after each sync and after library changes. Arr Companion reads that file. It finds Home Screen Companion's export folder by itself; a different file or folder can be set under Settings. For each title the file gives the lists it is on and its place on each one (`ranks: [{ list, rank, of }]`). If the file is from a Home Screen Companion version without places, Arr Companion takes them from the lists Home Screen Companion keeps (`missing_titles/sources.json`).
- **Missing tab.** Every title not in the library, with a poster, its best place and its place on each of its lists ("Trending #1"), and whether Sonarr or Radarr already has it: *+ Radarr* to add it, *✓ Radarr* when it is there (monitored, downloaded, or for a show how many episodes it has). It has a list view and a poster view. Filter by list, All / Movies / Shows and top places; *In Sonarr / Radarr* also shows the titles Sonarr or Radarr already has but the library does not have yet (the browser remembers the choice). Search by title or IMDb id. Click a row to select it. Add one title or a selection (root folder, quality profile, monitoring, search), or hide titles. The Add dialog starts on the root folder last added to on that server (kept in the plugin's settings), else the server's default from Settings; hidden titles are under *Show hidden*. Without a Sonarr or Radarr set up, everything works except the add itself, which says why it was not sent. The tab opens at once with what Sonarr and Radarr held the last time they were read (kept in the plugin's data folder, `arr_library.json`), reads them again meanwhile and updates the rows when they answer.
- **Language.** Each title shows its original language ("2026 · Series · Chinese · tt39368345"; on the poster as a small label), with its origin countries on hover, and the Missing tab filters by language ("Any language" and the languages present, with counts). They come from TMDb with the poster (`original_language`, `origin_country` / `production_countries`), looked up once in the background and kept in `posters.json`. Without a TMDb key, Emby's own metadata providers are asked instead; they give no original language, so it is then only known for titles whose original title is in its own script (Chinese, Japanese, Korean, Thai, Hindi, …), not for Latin-script titles.
- **Match.** *Match 82%* is how well a missing title fits what everyone on the server watched, from Emby's user data of all users (Played and LastPlayedDate, the last 180 days), read as Home Screen Companion's *Your Next Watch* reads it. Each user's watched movie or show weighs 0.5 + 0.5·e^(−days ago/60) (a show up to 2× by episodes watched, a favourite 1.5×, one rated below 5 not at all). The server's profile is the share of that watching per genre, original language, country and person (top-billed actors and directors). A title's score is on a fixed 0–100 scale: genres 40% (how much of the watching carries each, and how much more than the library carries it), language 25% and country 10% (their share of the watching; 40% or more counts fully; the language part only when the language of at least half the watching is known, so without a TMDb key it is left out), people 25% (people in it who are in the watched titles). A part without data is left out and the others count for it. The tooltip gives the reason ("Crime, Drama · Spanish"); *Best match* sorts by it. The profile is kept in `taste.json` and built again in the background when the tab opens and it is over 6 hours old (the refresh button: over a minute); the tab still opens at once with the kept scores.
- **Posters** come from TMDb, with Home Screen Companion's TMDb key unless one is set in Settings. Without a TMDb key, Emby's own metadata search (the one Identify uses) is asked by the title's ids. Each poster is looked up once and kept; the browser loads the image itself.
- **Rules.** A rule names a list (or any list), movies, shows or both, the top places to take, and optionally a first year, a limit per run, only these original languages, not these languages, and a lowest match ("match at least 60%"). A rule with a language or match condition leaves out titles whose language or match is not known yet; before a scheduled run it looks them up first. Rules run on the scheduled task *Arr Companion › Run rules* (every 6 hours) and, if that is on, a few minutes after the file changes. They use the server's default root folder and profile. *Preview* shows what a rule picks, also before Sonarr and Radarr are set up, and why it picks nothing when it does. A title a rule has added once is never added again, so one removed from Sonarr or Radarr stays removed.
- **History.** What was added, by hand or by which rule, and from which place on which list.

Shows already in the library, with missing episodes, are left out for now: version 1 adds whole series.

## Servers

Settings has one Sonarr and one Radarr. They are saved as a list (`Instances`, each with its own id), and adds and rules pick a server by id, so more than one of each kind only needs the page. Emby's server talks to Sonarr and Radarr. The API keys never reach the browser.

## Requirements
- HomeScreenCompanion v4.5.1.0 and above
https://github.com/soderlund91/HomeScreenCompanion
- Sonarr and/or Radarr with API v3.

## Install

Download the .dll file from latest releases. Place the .dll in you plugin folder. Restart Emby. 

Every push to main is built by GitHub Actions and published as a release (`0.2.0.<build>`, major and minor from the project's version).

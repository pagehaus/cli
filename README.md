# pagehaus

Preview, check and deploy plain HTML websites from the command line.

```
pagehaus serve [folder]     preview the site at http://localhost:8080, with Server Side Includes,
                            reloading the page when you save a file
pagehaus check [folder]     find broken links, missing files, links whose case doesn't match the file,
                            files too big, pages without a title or description
pagehaus login              save an API token for deploy
pagehaus deploy [folder]    upload the folder to your Pagehaus site
```

## Checking a site

```
$ pagehaus check
Checked /Users/sam/bakery: 5 pages, 7 files.

problem: links to /contact.html, which doesn't exist.
         in about.html, blog/first.html, blog/second.html and 1 more
problem: links to /img/team.jpg, but the file is named img/Team.JPG. That works on Windows and Mac, not on a web server: the case must match.
         in about.html, blog/first.html, blog/second.html and 1 more
problem: menu.html: links to file:///C:/Users/sam/Desktop/menu.pdf, which is on your computer: visitors can't reach it.
warning: menu.html: no <title>: search results and browser tabs show the address instead.

3 problems, 1 warning.
```

Pages are checked with their Server Side Includes filled in, so a broken link in a shared header is reported once,
with the pages it's on. File names are compared exactly, as a web server does, even on a Mac or Windows disk that
doesn't mind the case. The exit status is 1 when there are problems (with `--strict`, warnings too), so `check` can
stop a CI build. It works on any static site, not only Pagehaus ones.

## Deploying to Pagehaus

```
$ pagehaus login
Make an API token at https://pagehaus.app/account/api (for Access, choose "yoursite: read and write"), then paste it here.
Token:
Logged in with "laptop" (site bakery, can deploy).

$ pagehaus deploy --dry-run
Checking what would change on bakery: 12 files, 84.2 KB zipped...
  2 new (would be written): /menu.html, /img/croissant.jpg
  10 replaced (would be written): /index.html, /style.css, ...
Dry run: nothing was changed. Run without --dry-run to deploy.

$ pagehaus deploy
```

`deploy` runs `check` first and stops if it finds problems (`--force` deploys anyway). It uploads every file except
hidden ones (`.git`, `.env`...); files you deleted locally stay on the site, so delete those in the dashboard. With
several sites, say which: `--site bakery`. In CI, set `PAGEHAUS_TOKEN` instead of logging in. On GitHub, the
[Deploy to Pagehaus](https://github.com/marketplace/actions/deploy-to-pagehaus) action does it all in three lines. The saved token is in
`~/.config/pagehaus/config.json` (`%APPDATA%\pagehaus\config.json` on Windows), readable only by you.

## Server Side Includes

`serve` processes [Server Side Includes](https://pagehaus.app/help/server-side-includes) the way Apache's
mod_include does: `include`, `echo`, `set`, `config`, `flastmod` and `fsize` (no `exec`), with limits that keep
untrusted pages safe. It's the same engine Pagehaus hosting uses, so what you see is what you get there. The engine
is also a .NET library: `src/Pagehaus.Ssi`.

## Building

Needs the .NET 10 SDK. `dotnet test`, then `dotnet run --project src/Pagehaus.Cli -- serve path/to/site`.

MIT licence. Made by [Pagehaus](https://pagehaus.app), plain HTML hosting.

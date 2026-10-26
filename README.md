# pagehaus

Preview, check and deploy plain HTML websites from the command line.

```
pagehaus serve [folder]     preview the site at http://localhost:8080, with Server Side Includes,
                            reloading the page when you save a file
```

Coming next: `pagehaus check` (broken links, missing files, `Image.JPG` linked as `image.jpg`) and
`pagehaus deploy` (upload the folder to your [Pagehaus](https://pagehaus.app) site).

## Server Side Includes

`serve` processes [Server Side Includes](https://pagehaus.app/help/server-side-includes) the way Apache's
mod_include does: `include`, `echo`, `set`, `config`, `flastmod` and `fsize` (no `exec`), with limits that keep
untrusted pages safe. It's the same engine Pagehaus hosting uses, so what you see is what you get there. The engine
is also a .NET library: `src/Pagehaus.Ssi`.

## Building

Needs the .NET 10 SDK. `dotnet test`, then `dotnet run --project src/Pagehaus.Cli -- serve path/to/site`.

MIT licence. Made by [Pagehaus](https://pagehaus.app), plain HTML hosting.

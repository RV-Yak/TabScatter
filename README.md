# TabScatter

TabScatter is a Windows desktop app that opens four random URLs from `Links.txt`
in a tiled WebView2 interface. Refresh to pick another set, open a preview in
an in-app tab, or bookmark a URL to save it back to `Links.txt`.

## Features

- Four independent WebView2 previews and in-app tabs
- Back/forward navigation and URL tooltips
- Bookmark buttons append URLs to `Links.txt`
- Right-click **Block element on this site**, then click an element to hide it
- Site-specific element rules are stored in `BlockedElements.json`

The element blocker is part of the app; no browser extensions are included.

## Run from source

1. Install the .NET 8 SDK and Microsoft Edge WebView2 Runtime.
2. Edit `Links.txt` and add at least one HTTP or HTTPS URL per line.
3. Run `RunDesktop.bat` or `dotnet run --project TabScatter.csproj`.

## Publish a portable build

Run `PublishTabScatter.bat` to create a self-contained Windows x64 build in
`publish`. The WebView2 Runtime must still be installed on the target PC.

## Privacy and generated files

`Links.txt` contains only public example URLs in this copy. Replace them with
your own list locally, and review it before committing changes. The app creates
`BlockedElements.json` and `WebView2UserData` at runtime; these are ignored by
Git.

## License

This project is licensed under the MIT License. See [LICENSE](LICENSE).

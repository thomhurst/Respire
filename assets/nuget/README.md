# NuGet icon

`icon.png` is a 128 x 128 PNG rendering of `website/static/img/favicon.svg`,
using the docs site's existing colors and mark. All shipping packages embed it.

To regenerate after changing the SVG, render it at 128 pixels wide with an SVG
renderer (for example `@resvg/resvg-js` 2.6.2). Preserve the transparent rounded
corners and keep the PNG below NuGet's 1 MB limit.

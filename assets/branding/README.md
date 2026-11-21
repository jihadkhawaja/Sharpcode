# Sharpcode brand assets

Sharpcode’s mark is a mint, angular S on an ink-slate tile. The dark background and mint accent match the workbench palette.

| Asset | Use |
| --- | --- |
| [Icon master](sharpcode-icon.png) | Transparent 1254 × 1254 PNG for design and export |
| [Project banner](sharpcode-banner.png) | 1774 × 887 PNG for the README and a 2:1 social preview |
| [App icon PNG](../../src/Sharpcode/Assets/Sharpcode.png) | 256 × 256 source for the window, toolbar, and Linux archive |
| [Windows ICO](../../src/Sharpcode/Assets/Sharpcode.ico) | The executable icon with nine sizes from 16 to 256 px |
| [macOS ICNS](../../packaging/macos/Sharpcode.icns) | App bundle icon with standard and Retina sizes |

| Workbench color | Hex |
| --- | --- |
| Canvas | `#101219` |
| Panel | `#161923` |
| Mint accent | `#A8F5CB` |
| Text | `#E5E9F4` |

## Generation prompts

Both source images were created with built-in ImageGen. The banner used the selected icon as its reference. The app icon files are exports of the same transparent PNG.

### App icon

```text
Use case: logo-brand.
Asset type: source artwork for the Windows, macOS, and Linux desktop app icon of Sharpcode, a native code editor with a dark slate interface.
Create one original, memorable desktop app icon on a square canvas with a genuinely transparent background. The icon is a straight-on rounded-square tile, centered and occupying about 86 percent of the canvas width and height, with all four rounded corners clearly visible. Build the tile from deep ink-slate ceramic (#161923) with a quiet teal-black gradient and an exceptionally restrained beveled rim. Center one broad, custom angular S monogram. Construct the S out of two interlocking chisel-cut mint bands with a sharp diagonal negative-space cut at the waist, subtly recalling code chevrons. The S must read clearly as a single glyph when tiny. Clean silhouette, strong visual hierarchy, cool bright mint (#A8F5CB) with a subtle emerald tone at the lower edge, crisp antialiased edges. Refined near-flat studio lighting, minimal depth, no tilt; serious developer tool with a precise distinctive mark. Wide and even internal padding. Deliver a single finished production icon and nothing outside the tile. No words, additional letters, hashtags, overlays, extra symbols, tiny markings, external shadow, outline glow, checkerboard, product mockup, or resemblance to an existing editor logo.
```

### Project banner

```text
Use case: logo-brand.
Asset type: wide repository banner and social preview art for Sharpcode, a native code editor.
Input image: the Sharpcode rounded-square app icon is the brand reference. Preserve the distinctive geometry and mint gradient of its angular S exactly as closely as possible.
Create a finished horizontal developer-tool brand lockup on a 2:1 landscape canvas. Put the reference icon, facing straight on, immediately to the left of a large wordmark and center the combined lockup on the canvas. Set the wordmark exactly as “Sharpcode” in a crisp, modern, slightly compact sans-serif in near-white (#E5E9F4) with excellent legibility. The monogram tile and wordmark are the only foreground elements. Use a very dark ink-slate backdrop (#101219), gently lit in deep teal at the center, with a few understated incised diagonal paths inspired by the monogram, fading toward the side edges. Limit accent color to the icon's bright mint and traces of cool emerald in the background. Premium, spare, tactile, high contrast, and suitable above the README of a serious open-source editor. Keep the entire lockup within the central 72% of the canvas, with generous padding above, below, and on both sides, so the banner can be cropped safely for a social preview. No other text, tagline, secondary logo, screenshots, code blocks, decorations that resemble UI controls, noisy texture, mockups, or watermarks.
```

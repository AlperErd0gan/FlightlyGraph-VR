# Intro background video (AI generated): concept, tool, prompts

Backlog: TASKS B40. Not started; nothing here is built or tested in the headset.

## What it is (and is not)

A **slow, dark, looping background** for the start / intro screen (`StartAmbience`), shown far
behind the live 3D animation (airports, aircraft, trails). It is atmosphere, not the intro itself:

- not a flat video screen in front of the user (low immersion, blurry on Quest, breaks the link to the real graph);
- not a replacement for the Wit.ai welcome or the data-driven aircraft;
- no camera rotation, no fast motion, no flashes (vertigo, Reduce motion, apple-design: restraint);
- very low contrast and dim, so the foreground animation stays the focus and the scene stays "slightly dim".

## Look

- Deep night blue / near black, a calm "European air traffic at night" feeling.
- Tiny soft glowing points (airports) and fine thin luminous arcs (routes), drifting very slowly.
- A few very small aircraft lights (white, red, green) far away, blinking gently.
- Faint haze / depth, a hint of Earth's curve or city-light clusters in the lower part is fine.
- Palette matches the app: dark background, cool white / pale blue / warm amber accents (see `UIKit` colours).
- Camera: locked off, or a very slow forward drift (under 2 % zoom over the whole clip). No pan, no roll.
- No text, no logos, no people, no real airline liveries, no UI.

## Technical spec

| Item | Value |
|---|---|
| Length | 8 to 12 s, **seamless loop** |
| Resolution | 3840 x 2160 (4K) if possible, 1920 x 1080 minimum |
| Frame rate | 24 or 30 fps |
| Audio | none (Wit speaks over it) |
| Format for Unity | MP4, H.265 (HEVC) for Quest; H.264 fallback. Keep under about 20 MB |
| Brightness | mean luminance low; edges darker (vignette) so it fades into the scene |

## Tool

Pick one, then check the current version, price and licence on the day (they change fast).

1. **Runway Gen-4 (image-to-video)**: first make a still with the image prompt below (any image tool), then animate it.
   Use the *same image as first and last frame* for a loop if the tool offers it.
2. **Google Veo (text-to-video)**: best for the moving glow and soft depth; can also do audio, which we do not want (turn off or strip).
3. **Luma Ray**: has a loop option; good for abstract light motion.
4. Fallbacks: Kling, OpenAI Sora.

If the result does not loop: make it ping-pong (forward then reversed) or cross-fade the last 1.5 s into the first
1.5 s with `ffmpeg` / DaVinci Resolve (free). Upscale to 4K with Topaz Video AI if the tool only gives 1080p.

Recommended start: **Runway Gen-4, image to video, same first/last frame**, because the loop and the dark abstract
look are the hardest parts and that gives the most control.

## Prompts

### A. Still image (for image-to-video; any image generator)

```
A calm abstract night view of European air traffic seen from high above: a deep navy to near-black background,
thousands of tiny soft glowing points of pale blue and warm amber light (airports) connected by very thin
luminous curved arcs (flight routes), a few tiny distant aircraft lights, faint atmospheric haze and gentle depth
of field, a barely visible curve of the Earth at the bottom, vignette, low contrast, minimal, elegant, cinematic,
dark mode, 16:9, no text, no logos, no people.
```

### B. Video prompt (image-to-video or text-to-video)

```
Very slow, calm, seamless loop. Tiny glowing points of pale blue and warm amber light twinkle softly over a dark
navy night scene; very thin luminous arcs between them shimmer and slowly pulse as if traffic flows along them;
a few small distant aircraft lights drift slowly across the far background and blink gently. A faint haze drifts.
Camera completely still, or an extremely slow forward drift. Dark, low contrast, soft glow, elegant, minimal,
cinematic, no sudden changes, no flashes, no camera shake, no rotation. 4K, 24 fps, 10 seconds, perfectly
looping, last frame matches the first frame.
```

### C. Negative prompt (if the tool has one)

```
text, logos, watermark, people, faces, real airline names or liveries, bright flashes, lens flare bursts,
fast motion, camera shake, camera rotation, spinning, strobing, high contrast, oversaturated colours, daylight,
clouds filling the frame, cartoon, low resolution, blur, artifacts
```

### D. Variant: map feel (if A/B look too abstract)

```
Top-down night view of Europe as a dark minimal map made of tiny glowing city lights, with thin pale-blue arcs
connecting the brightest hubs, slow gentle shimmer along the arcs, a few tiny aircraft lights moving very slowly,
soft haze, dark navy background, low contrast, locked camera, seamless 10 second loop, no text, no borders, no logos.
```

## Check the result before using it

- [ ] Loops without a visible jump (watch the loop point 5 times).
- [ ] No fast movement, flashes or rotation; fine with Reduce motion on.
- [ ] Dark and low contrast: lay it behind `StartAmbience` and the aircraft / trails must still stand out.
- [ ] No text, logos or real brand marks; nothing that looks like real flight data.
- [ ] 4K file under about 20 MB; plays on Quest 3 without frame drops (PerfMonitor, B22 / B39).
- [ ] Licence: the tool's plan allows use in a published app / paper; note the tool and version for the paper
      ("background video generated with <tool>, <date>").

## Unity plan (when the clip exists)

- Put the file in `Assets/StreamingAssets/` (or `Resources`), as `intro_background.mp4`.
- `StartAmbience` gets an optional background: a `VideoPlayer` (render to texture, looping, no audio) on a large
  inward-facing sphere / curved quad about 30 m away, with a dark vignette material, fading in and out with
  `Wanted` / `Progress` like the rest. Off in Reduce motion (still first frame) and in the Desktop condition if it
  costs frames; `useBackgroundVideo` field to turn it off.
- Quest: H.265 hardware decode, check frame time in PerfMonitor with the video on and off.
- Keep it behind the scene dimming: scale its brightness with `dimLevel`.

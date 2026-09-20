---
name: ring-image
description: Design a new image for Floaty's floating ring overlay and apply it live. Use when the user asks to change, restyle, redesign, theme, randomize or "make new" the ring, the floaty, the orb, or the overlay itself — or asks what Floaty could look like.
license: MIT
compatibility: Designed for Floaty. Requires a provider assigned to the Image role in Settings, Model provider. Writes into ~/.floaty/ring and changes the saved ring selection.
metadata:
  author: floaty
  version: "1.0"
---

# Restyling Floaty's ring

The ring is the circular thing floating on the user's desktop — Floaty's whole visible presence.
Replacing it is two tool calls and takes effect immediately.

## 1. Generate

Call `generate_image` with `size: "1024x1024"` and `transparent: true` — the ring is always square,
and its corners must not be visible.

Build the prompt from this template, substituting the user's idea:

> A circular ring (torus, donut shape) seen head-on, centred in frame, {the user's idea}. The ring is
> thick and clearly readable at small sizes. The centre of the ring and everything outside it is fully
> transparent. No text, no lettering, no background, no drop shadow, no border, no frame.

To restyle the ring that is already there rather than invent a new one, call `edit_image` on the
current ring file with the same constraints appended.

## 2. Apply

Call `set_ring_image` with the file name `generate_image` returned. The overlay updates on the spot —
no restart — and the choice persists across launches.

## 3. Confirm

One short sentence. Offer one variation they might want next.

## Why the constraints matter

These aren't style preferences; each one is a property of how the overlay actually renders.

- **The corners are dead.** The overlay hit-tests a circle inscribed in the image, so anything drawn
  outside that circle is visible but not clickable. Keep the design inside the circle.
- **It renders as small as 50 px.** Fine detail, thin lines, gradients with low contrast and text all
  disappear. Bold shapes and strong value contrast survive.
- **The hole matters.** A filled disc reads as a blob, not as Floaty. Keep the centre open.
- **Without transparency it's a tile.** A non-gpt-image model returns an opaque square. Apply it
  anyway if the user likes the artwork, but tell them why it has corners and what would fix it.

## Edge cases

- **"Go back to the duck one" / "use the default one."** Skip generation entirely and call
  `set_ring_image` with the built-in's name. They are `daisy.png` (blue ring with daisies),
  `tropical.png` (floral print ring), `duck.png` (rubber-duck float), `donut.png` (pink sprinkled
  donut), `gold.png` (engraved gold band), `wheel.png` (chrome car wheel) and `moon.png` (full moon).
- **No image model configured.** Say so and point at Settings → Model provider → Roles → Image. The
  built-in rings still work through `set_ring_image` in the meantime.
- **"Surprise me."** Pick a concrete direction and commit to it — a material (brushed copper, sea
  glass, neon tube), a subject (an ouroboros, a laurel wreath, a planetary ring) — rather than asking
  what they had in mind.

---
name: generate-image
description: Create and edit images with Floaty's configured image model (OpenAI gpt-image-1 or dall-e-3, Google gemini-2.5-flash-image "nano banana", or any OpenAI-compatible image endpoint). Use when the user asks to draw, paint, render, illustrate, design or generate a picture, logo, icon, avatar, sticker, wallpaper or concept art, or to restyle an image they already have.
license: MIT
compatibility: Designed for Floaty. Requires a provider assigned to the Image role in Settings, Model provider, and network access to it.
metadata:
  author: floaty
  version: "1.0"
---

# Making images

Two tools. `generate_image` makes one from a description; `edit_image` restyles one that already
exists. Both save a PNG into `~/.floaty/generated` and Floaty displays it in the chat by itself.

## Writing the prompt

Expand what the user asked for into a full visual description. Do not interrogate them first — one
good image they can react to beats three clarifying questions.

Cover, roughly in this order: **subject**, what it is doing, **setting**, **art style or medium**,
**composition and framing**, **lighting**, **colour palette**. Two or three sentences beats a list of
comma-separated tags.

> "a cat" → "A ginger tabby curled asleep on a windowsill, seen from the side. Loose watercolour on
> cold-press paper, visible brush texture, soft afternoon light from the left, warm ochres and dusty
> blues, generous negative space around the subject."

Say what you want, not what you don't. Negative phrasing ("no text", "not blurry") only reliably
works for the specific things listed under the ring skill's constraints.

## Choosing a size

| `size`        | Use for |
| ------------- | ------- |
| `1024x1024`   | Default. Avatars, icons, logos, album covers, the ring. |
| `1536x1024`   | Landscape: scenes, banners, desktop wallpapers, headers. |
| `1024x1536`   | Portrait: characters, posters, phone backgrounds. |

Omit it entirely when the user expressed no preference and the subject has no obvious orientation.

## Transparency

Pass `transparent: true` for logos, icons, stickers, badges, and anything meant to sit on top of
something else. Only gpt-image models honour it — everything else silently returns an opaque
rectangle. When that happens, say so in one sentence and offer to switch models; do not retry the
same call hoping for a different result.

## Editing instead of regenerating

Reach for `edit_image` when the user is reacting to something that already exists — "make it warmer",
"same but at night", "turn my screenshot into a poster". It accepts:

- a file name returned by `generate_image` or a previous `edit_image`
- a screen capture from Floaty's memory (the `file:` value in a `search_captures` result)
- a file the user dropped on Floaty
- a built-in ring, `ring1.png` through `ring7.png`

Describe the **finished** image, not the delta: "the same fox, now under a full moon with cool blue
shadows" works; "make it night" is weaker.

Not every provider implements the edit endpoint. If the tool reports that, generate a fresh image
with the change folded into the prompt instead, and mention the substitution.

## After the image lands

- Don't paste base64. Don't repeat the file name unless the user needs it for something.
- Reply with one line: what you made, plus one concrete thing you could change on request.
- If they want it as Floaty's ring, follow the `ring-image` skill.

## Edge cases

- **Generation failed.** Relay the tool's message. If it says no image model is configured, point at
  Settings → Model provider → Roles → Image.
- **Several variations wanted.** Call `generate_image` once per variation, up to three, varying one
  named thing between them so the choice is meaningful.
- **A refusal from the provider** (a real person, a trademark, a restricted subject) is not a bug.
  Say what was refused and offer the nearest thing that isn't.

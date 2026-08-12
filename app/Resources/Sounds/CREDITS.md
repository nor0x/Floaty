# Built-in sound effects

Every sound shipped in this folder comes from [BigSoundBank](https://bigsoundbank.com) (Joseph
Sardin / LaSonotheque), which releases its library under **CC0 1.0 — public domain**. No
attribution is required; this file exists so the provenance stays traceable.

> "These sounds are released under a public-domain equivalent license (CC0 / WTFPL / public
> domain)." — <https://bigsoundbank.com/licenses.html>

| File | Source sound | Source page |
| --- | --- | --- |
| `shutter.wav` | Triggering camera (single SLR trigger) | <https://bigsoundbank.com/triggering-camera-s0307.html> |
| `shutter-double.wav` | Two triggers camera | <https://bigsoundbank.com/two-triggers-camera-s0308.html> |
| `camera-phone.wav` | iPhone — Camera | <https://bigsoundbank.com/iphone-camera-s0448.html> |
| `notify.wav` | Notification "LaSoLisa" #4 | <https://bigsoundbank.com/notification-lasolisa-4-s2066.html> |
| `chime.wav` | Idea #2 (metallophone) | <https://bigsoundbank.com/idea-2-s1399.html> |

## Processing

The originals are 44.1/48 kHz, 16/24-bit, mono or stereo, and several carry long silent tails.
Each was trimmed to the useful hit, faded out to avoid an end-of-buffer click, peak-normalised to
≈ −3 dBFS, and converted to **16-bit PCM mono 44.1 kHz** — small enough that every byte here can
ride along in each Velopack package, and a format `WindowsSoundService` can decode without a
codec. `camera-phone.wav` is the first of the source's two shutter clicks; `chime.wav` is the
first 1.6 s of the metallophone with a 300 ms fade.

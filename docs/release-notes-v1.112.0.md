# DiscForge — What's New (v1.112.0)

## You can now build an audio CD from FLAC — and from MP3, AAC, OGG, WMA and more

Creating an audio CD used to mean starting from a plain WAV file — nothing else was accepted. Now
you can hand it a FLAC file directly and it decodes it losslessly itself, no extra software needed.
If you've got MP3, AAC/M4A, Ogg Vorbis, WMA, APE, Musepack or WavPack files instead, it'll convert
those too, as long as you have FFmpeg installed (a free, separate download — DiscForge doesn't
bundle it, the same way it doesn't bundle any other outside tool). If FFmpeg isn't found, you get a
plain message saying so instead of a confusing failure.

Whatever the source format, it still only ever makes an audio CD that's genuinely correct: 44.1kHz,
16-bit, stereo. A file that isn't in the right format after conversion is refused with a clear reason
rather than quietly resampled into something wrong.

## The Burn screen can now queue up several different discs to burn one after another

Previously, Burn's "several destinations at once" feature meant burning the *same* image to several
drives simultaneously — useful, but not the same thing as burning a stack of *different* discs
without babysitting each one. Now there's a queue: add as many images as you like, and Burn works
through them in order, pausing to ask you to swap the disc between each one. If one image in the
queue has a problem, it's skipped and logged — the rest of the queue keeps going instead of stopping
cold. Tested for real against a Plextor drive: queuing several images and running a normal single
burn both worked cleanly.

## Burn now suggests a sensible write speed instead of defaulting to "as fast as possible"

Blank DVDs and Blu-rays often carry a certified speed rating as part of their manufacturing code (for
example, a disc might identify itself as "Taiyo Yuden 16×"). When DiscForge recognizes that code, it
now defaults the write speed to that rating instead of leaving it on "Max" — the same idea as buying
a disc rated for a certain speed and actually burning it at that speed, rather than pushing the drive
past what the media was made for. If the disc's code isn't one DiscForge recognizes yet (a fairly
small, curated list so far), it's left alone on "Max" exactly as before — no guessing, no invented
recommendation.

This doesn't apply to CD-R: unlike DVD/Blu-ray, a CD-R's manufacturing code identifies who made the
disc but never encodes a speed rating, so there's honestly nothing to read there.

## Everything else

These three were the concrete gaps identified in a full feature-by-feature comparison against
ImgBurn (`docs/imgburn-comparison.md`), written up separately. Verified with 11 new automated tests
(2,749 total, all green) plus a real run against a Plextor PX-891SA drive. `DiscForge.App` requires a
real Windows build environment to compile and run — confirmed working via that hardware run.

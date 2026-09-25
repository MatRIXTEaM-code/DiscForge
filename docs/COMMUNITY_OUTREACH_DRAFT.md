# Community outreach drafts — DiscForge

> **Out of date (v1.115.0):** written for the GPL release. DiscForge is now closed-source freeware and the
> repository is private — rewrite the licence and link details before using any of this.

*Drafts only — nothing here has been posted. Review and edit before sending; each community has its
own norms (Redump especially is strict about self-promotion and evidence-first claims), so these are
written humble and falsifiable rather than as marketing copy. Swap in the real repo link, your own
voice, and screenshots/GIFs before posting. Repo: https://github.com/MatRIXTEaM-code/DiscForge*

---

## 1. Redump forum post (most important, most sensitive)

Redump's community trusts nothing on reputation — only on reproducible evidence, and they're
justifiably allergic to tools showing up claiming to be the next big thing. This draft leads with
what's checkable, invites people to break it, and explicitly doesn't ask them to switch tools.

> **Subject: DiscForge — an open dumping/preservation tool with signed dump certificates + a public
> cross-verification ledger (feedback wanted, not a pitch)**
>
> Posting this here specifically because Redump is the place that would notice if this is wrong.
>
> DiscForge (GPL-3.0, https://github.com/MatRIXTEaM-code/DiscForge) is a disc preservation/imaging
> tool I've been building. Rather than describe features, here's the one thing I think is genuinely
> new and worth this community's scrutiny: `dump-ledger`.
>
> The problem it's aimed at: Redump's "is this dump good" answer today is a human maintainer
> comparing submissions. That works, but it's not independently checkable by someone outside the
> maintainer team, and there's no cryptographic record that two people who dumped the same disc
> independently actually got the same bytes. `dump-ledger` is an attempt at making that answer a
> public, self-verifying artifact instead: every dump gets a signed certificate (SHA-256 + Merkle
> root over the image), and the ledger hash-chains certificates from independent submitters of the
> same disc so anyone can verify agreement (or catch disagreement) without trusting a maintainer's
> word for it. It's meant to sit alongside Redump's process, not replace it — the DAT/submission
> workflow (`redump-cue`, `redump-diff`, `redump-prep`) is built to produce Redump-conventional
> output, not a competing standard.
>
> Also relevant to this crowd specifically: subchannel Q-based gap recovery (`subq-map`), a
> bad-sector-aware multi-copy merge that emits an auditable reconstruction certificate
> (`merge-cert`), and a `redump-diff` that explains *why* a dump doesn't match (split, padding,
> offset, bad sector) instead of just failing a checksum.
>
> I have real-hardware validation on one Plextor drive and one disc so far (details in the repo's
> `docs/DIFFERENTIATORS.md`) — that's a thin sample and I know it. What I'd actually value from this
> forum: does the ledger's cryptographic model hold up, are there disc classes where `subq-map`'s
> INDEX 00/01 recovery would obviously break, and is there interest in a few independent dumps run
> through it to see if the ledger produces sane agreement/disagreement calls. Not asking anyone to
> adopt it — asking whether the idea is sound.

---

## 2. Reddit — r/DataHoarder

DataHoarder is receptive to "one tool replaces several tools" pitches and to provenance/integrity
angles, less allergic to a direct comparison than Redump is.

> **Title: I built a disc-preservation tool with signed dump certificates + a Merkle-provable public
> ledger — feedback welcome**
>
> DiscForge (GPL-3.0, open source): https://github.com/MatRIXTEaM-code/DiscForge
>
> Most disc tools fall into one lane — burn (ImgBurn/AnyBurn), dump (Aaru/Redumper), or convert
> (a pile of single-purpose CLI tools). DiscForge tries to do all three in one engine, cross-platform:
>
> - Preservation-grade reading: adaptive re-read escalation, interrupt/resume, RAW + full subchannel
>   capture, per-sector confidence tracking (not just pass/fail).
> - RAW DAO-96 burning natively over SPTI/MMC — no shelling out to an external tool, works the same
>   way on Windows/macOS/Linux.
> - Format conversion across BIN/CUE, CCD, CDI, GDI, MDS, NRG, CHD (create *and* extract), CSO/ZSO,
>   WBFS.
> - Signed dump certificates + a public hash-chained ledger, so independent dumps of the same disc
>   can be cross-verified cryptographically instead of just trusted.
> - Longitudinal disc-health tracking (Disc Actuary) — track a specific physical disc's degradation
>   across repeated scans over months/years, not just a one-time quality snapshot.
>
> Honest limitations: it's less battle-tested than Aaru/Redumper on raw CD dumping specifically —
> they've got years of community-contributed drive/disc coverage I don't have yet. And the GUI is
> WinForms with a deliberately retro CDRWIN-4-era look, which is either charming or off-putting
> depending on taste. CLI is 280+ commands and cross-platform; GUI is Windows-only for now.
>
> Would genuinely appreciate anyone willing to run it against a disc they've already got a trusted
> dump of, and tell me where it disagrees or falls over.

---

## 3. Reddit — r/Roms or r/emulation (shorter, format-conversion-angle)

> **Title: Open-source tool that creates CHD directly from a disc (plus reads/writes GDI, CSO/ZSO,
> WBFS, and does RAW disc dumping) — DiscForge**
>
> https://github.com/MatRIXTEaM-code/DiscForge (GPL-3.0)
>
> If you've ever wanted to go disc → CHD without a two-step dump-then-convert dance, or needed a
> single tool that handles GDI/CSO/ZSO/WBFS conversion plus actual preservation-grade disc reading
> (adaptive re-read, subchannel capture, per-sector confidence), this might be useful. It also does
> DVD-Video/BDMV authoring with IFO-pointer repair and layer-break planning, if that's ever come up
> for you. CLI works on Windows/macOS/Linux; there's also a Windows GUI.
>
> Not trying to replace CHDMAN for people who already have a working pipeline — just flagging it in
> case the "one tool instead of four" angle is useful to someone here. Feedback and bug reports
> genuinely wanted, especially disc types I haven't tested against.

---

## 4. Vogons (retro-PC/CD hardware community — good fit for the RAW-write and drive-quirk angle)

> **Subject: DiscForge — native RAW DAO-96 writing over SPTI, cross-platform, looking for testers
> with unusual drives**
>
> https://github.com/MatRIXTEaM-code/DiscForge — GPL-3.0, CLI + Windows GUI.
>
> Posting here because Vogons has more real optical-drive hardware diversity than most places I'd
> think to ask. DiscForge does native RAW DAO-96 burning (full 2352+96-byte subchannel) directly over
> SPTI/MMC commands — no dependency on cdrdao or a similar external engine — plus preservation-grade
> reading with adaptive re-read and a drive-capabilities knowledge base keyed by model (a Plextor
> PX-W5224A/TA profile is built in as the reference case). If anyone's got an unusual drive sitting
> around and is willing to run `dforge drives` and a test dump/burn, I'd like to grow that knowledge
> base with real quirks instead of guesses — that's the single thing the more established tools
> (Aaru, Redumper) have that DiscForge doesn't yet: years of contributed drive data.

---

## Notes on sequencing

Post to Redump first and wait for responses before the wider Reddit posts — if something in the
ledger's cryptographic model or `subq-map`'s gap recovery turns out to be wrong, better to hear that
from the most technically exacting audience before a larger crowd sees the claims. The Vogons post is
the lowest-risk one to send anytime since it's explicitly a hardware-data request, not a capability
claim.

# Profiles

A Profile is the main way Naut gives a collection identity. It can represent a person, character, project, object, subject, or anything else you want to keep together.

## Profile identity

A Profile can have:

- a name, category, and tags;
- a Cover and optional Banner;
- favorite and rating state;
- a private note;
- related Profiles;
- a presentation Frame and layout;
- media attached to the Profile;
- an optional interactive 3D Figure when suitable model media is available.

## Cover and Banner

Cover and Banner are presentation choices, not replacements for the original media. Their framing can be adjusted in Customize, and they can be changed after import.

Video Banners can loop on the Profile surface when ready. Video hover presentation is intentionally brief and bounded.

Since v0.0.9, each Profile can retain an ordered collection of Banner alternatives. Use **Add as Banner** in Customize to prepare an additional eligible video without replacing the active choice. Choosing an active Banner, framing and effects is a preview until **Save**; **Cancel** discards those preview changes. When a selected Banner rendition is unavailable or requires repair, Naut falls back to a still image without discarding the saved selection.

In Naut v0.0.11, a ready video Banner resumes after Profile/Customize header reflow. Import pressure alone reduces decorative presentation detail instead of automatically stopping a selected Profile Banner; inactive windows, Reduced Motion and memory pressure still use a static poster when appropriate.

## Face-aware Profile suggestions

For applicable media, Naut can use [Face Intelligence](face-intelligence.md) to detect faces and compare compatible embeddings with locally confirmed identity samples.

A matching score can produce a **Profile candidate**, but a candidate is not the same thing as a confirmed identity. Explicit confirmation remains the durable decision. Confirmed face evidence can then participate in the relationship evidence Naut keeps between Profiles.

This design lets Naut assist with organization without silently treating a model suggestion as final truth.

## Media

Profile media keeps its own metadata and can be browsed in the Profile media area. Opening an original media item uses the normal Windows application associated with that file type.

## Editing, notes and removal

Use **Edit details** for Profile identity fields. Managed NORMAL Profiles can mirror Overview and Private Note into readable UTF-8 files in the Vault. External changes are reviewed explicitly rather than silently overwriting Naut's catalog. Because these files are plaintext, protect the filesystem and Vault backup accordingly.

Deleting a Profile creates a recoverable **30-day Group Trash** entry for the Profile and its exclusively owned media. Media still used by another active Profile remains accessible; restoring a group preserves later decisions affecting shared media. Permanent deletion cannot be undone.

Recent Media visibility can be changed for a Profile independently of the selected Portrait, Dossier or Chronicle layout.

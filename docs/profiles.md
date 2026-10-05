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

## Face-aware Profile suggestions

For applicable media, Naut can use [Face Intelligence](face-intelligence.md) to detect faces and compare compatible embeddings with locally confirmed identity samples.

A matching score can produce a **Profile candidate**, but a candidate is not the same thing as a confirmed identity. Explicit confirmation remains the durable decision. Confirmed face evidence can then participate in the relationship evidence Naut keeps between Profiles.

This design lets Naut assist with organization without silently treating a model suggestion as final truth.

## Media

Profile media keeps its own metadata and can be browsed in the Profile media area. Opening an original media item uses the normal Windows application associated with that file type.

## Editing and removal

Use **Edit details** for Profile identity fields and presentation-related details that belong there. Deleting a Profile sends it through Naut's Trash lifecycle rather than immediately destroying durable media.

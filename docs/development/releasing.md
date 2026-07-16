# Writing an Optimisarr release

Release notes should help someone answer three questions quickly:

1. What will I notice?
2. Is the update safer or easier for me?
3. Do I need to do anything before or after updating?

They are not a dump of commits. `CHANGELOG.md` remains the complete technical
record; the GitHub Release is the clear, human introduction to it.

## Start with evidence

Write from the versioned changelog and the checks completed for the exact tag.
Every claim must describe behaviour that is present in the released image. Do not
turn roadmap work, an open pull request, or an unverified host test into a release
promise.

Before writing, collect:

- the previous and new tags;
- the matching `CHANGELOG.md` section;
- the green tag build, test, container smoke test, and image publication;
- any migration, configuration, downtime, or compatibility action;
- the most important safety boundary or opt-in default affected by the release.

## Write for the update decision

Start from [the release-notes template](../../.github/RELEASE_NOTES_TEMPLATE.md).
Delete its comments and any section that has nothing useful to say.

Choose three to six changes a user will actually notice. Lead with the outcome:

> **More accurate storage checks.** Optimisarr now checks free space on the
> filesystem mounted at `/work`, so a small container disk no longer pauses a
> healthy queue.

Avoid leading with the implementation:

> Added mount-aware `DriveInfo` selection and orphan reconciliation.

The implementation belongs in the changelog. Release notes should still be
specific—“better performance” is not useful unless they say what became faster,
when, and whether there is a tradeoff.

## Voice and structure

- Use a calm, conversational tone. Write to `you`, not to “users”.
- Prefer short sentences and one idea per bullet.
- Use exact UI labels and familiar product terms.
- Put the benefit in bold at the start of each bullet.
- Describe safety honestly: what is protected, what is retained, and what remains
  opt-in.
- Put required action under **Before you update**, where it cannot be missed.
- Say **No special steps** when a normal image update is genuinely sufficient.
- Avoid hype such as “massive”, “game-changing”, and “best ever”.
- Avoid “we”; name Optimisarr or speak directly to `you`.

## Publish checklist

- [ ] The release title and tag match the application version.
- [ ] The exact tag's CI and container smoke test are green.
- [ ] Every statement is supported by shipped code, tests, or verified operation.
- [ ] The opening summary makes sense without reading the changelog.
- [ ] Bullets describe user outcomes rather than internal components.
- [ ] **Before you update** states every required action—or says none is needed.
- [ ] Defaults, opt-in features, safety limits, and performance costs are honest.
- [ ] No token, private hostname, real library path, or other secret appears.
- [ ] The full changelog link points at the released tag.
- [ ] Empty template sections and all HTML comments are removed.
- [ ] The rendered GitHub preview has been read from top to bottom.


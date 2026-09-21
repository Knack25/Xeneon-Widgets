# Planner Task Organization Design

## Goal

Bring the most useful basic Microsoft Planner organization controls to the
Planner Edge widget without turning the XENEON EDGE display into a full Planner
web client. The widget remains touch-first and glanceable while supporting the
task metadata and filtering needed for daily board management.

This release adds priority, progress, start dates, existing plan labels, task
title editing, full checklist editing, persistent filters, persistent My tasks
state, and faster resume from the helper's saved board snapshot.

## Scope

### Included

- Display and edit task title, progress, priority, and start date.
- Display the plan's existing labels and assign or remove them from tasks.
- Create tasks with title, bucket, assignees, start date, due date, priority,
  and labels. New tasks always begin as Not started.
- Add, rename, delete, complete, and reorder checklist items.
- Filter by assignee, label, priority, bucket, progress, and due-date range.
- Search task titles during the current widget session.
- Persist structured filters and My tasks independently for each plan.
- Restore the cached board and saved view state immediately, then refresh from
  Microsoft in the background.
- Preserve horizontal board position and per-bucket vertical positions across
  refreshes and widget reloads.

### Excluded

- Renaming plan labels. Label names remain managed in Microsoft Planner.
- Persisting title search across widget restarts.
- Editing Planner premium fields or views, including dependencies, timeline,
  goals, sprints, milestones, custom fields, or assignments effort.
- Planner calendar and charts views.
- Uploading files or managing task attachments.
- Applying the fast-resume change to the Outlook widget in this release.

## Interaction Model

### Board toolbar

The existing board picker, My tasks control, and New task control remain. Add a
filter control and a search control to the top-right toolbar. The filter control
shows the number of active structured filters. My tasks retains its active
state after reload and is saved per plan.

The filter panel contains touch-sized multi-select sections for assignee,
label, priority, bucket, progress, and due-date range. Clear filters resets the
saved structured filters for the selected plan. Switching plans restores that
plan's own filter set and My tasks state. Invalid saved values are discarded
when their member, label, or bucket no longer exists.

Search matches task titles case-insensitively. Search text lasts only for the
current widget session and clears on reload. Search and structured filters can
be combined. Filtering never changes Microsoft task or bucket ordering.

### Task cards

Task cards retain the large primary completion checkbox and its confirmation
dialog. Tapping anywhere else opens task details.

Cards add compact metadata:

- Urgent and Important priority indicators; Medium and Low remain available in
  details and filters without adding prominent card noise.
- Colored indicators for assigned labels.
- Progress state when In progress. Completed tasks continue to follow the
  helper's Hide completed tasks preference.
- Existing due date and checklist preview.

### Task details

The task title is editable. Details include controls for progress, priority,
start date, due date, assignees, bucket, and existing plan labels. Progress has
Not started, In progress, and Completed values. Choosing Completed through the
progress control uses the same explicit confirmation expectation as the large
completion checkbox.

The checklist supports adding, renaming, deleting, completing, and reordering
items. Deletion requires confirmation. Reordering must work without depending
on precise touch dragging; move-up and move-down controls are the reliable
baseline, with drag support permitted only if it does not interfere with
scrolling on the EDGE.

Existing notes and task chat remain below the checklist. Existing outside-tap
closing, scroll behavior, assignment editing, bucket movement, due-date editing,
and chat behavior must remain intact.

### New task

The New task dialog includes title, bucket, assignees, start date, due date,
priority, and labels. Progress is not selectable during creation and defaults to
Not started. Title and bucket remain required. Start date, due date, assignees,
priority, and labels are optional; priority defaults to Planner's Medium value.

## Helper Architecture

Follow the existing focused-service and focused-route pattern. Do not expose a
generic arbitrary Planner patch endpoint.

Expand board and task detail contracts with:

- `priority`
- `percentComplete`
- `startDateTime`
- applied label identifiers
- plan label descriptions and their category identifiers

Add narrowly validated operations for:

- renaming a task
- changing progress
- changing priority
- setting or clearing a start date
- assigning or removing existing plan labels
- adding, renaming, deleting, completing, and reordering checklist items

Shared internal update helpers may consolidate ETag acquisition, selected-plan
validation, Graph PATCH creation, cache invalidation, and error translation.
Every write must confirm that the task belongs to the selected plan and must use
the latest applicable ETag. Task-detail changes use the task-details ETag;
top-level task changes use the task ETag.

The existing delegated `Tasks.ReadWrite` permission covers these basic Planner
updates. This release must not add an organizational consent requirement.

## Label Handling

Load label descriptions from the selected plan's details and expose only labels
that have names. Preserve Planner category identifiers so applied labels map to
their correct colors. Users can assign or remove existing labels but cannot
rename them in the widget.

If Planner returns an applied category whose description is blank or was
removed, omit it from the visible filter list and sanitize it from saved filter
state. Do not alter that category on the task unless the user explicitly saves
new label assignments.

## Persistent View State

Store structured filters and My tasks by plan ID in the helper's local settings.
The settings schema must tolerate upgrades from installations that do not have
these values. Saving one plan's state must not overwrite another plan's state.

Persist horizontal board position and each bucket's vertical position as
lightweight widget view state. Do not store Planner task content in browser
storage. Missing or malformed view state falls back to zero without blocking
the board.

## Fast Resume

The existing display route waits for a live Graph request before returning the
saved snapshot on failure. Add a cache-only display route that reads the
helper-owned saved board without contacting Microsoft.

Widget startup performs these steps:

1. Request the cache-only display and render it immediately when available.
2. Restore plan-specific filters, My tasks, and saved scroll positions.
3. Start the normal live display request in the background.
4. Replace the board data in place when the live request completes, while
   preserving scroll position, active filters, dialogs, and drafts.
5. Keep the cached board visible with an Offline view indicator if refresh
   fails.

The cached board shows its original sync time. A live refresh updates the sync
time and clears the stale indicator. The helper remains the only persistent
owner of Planner board data.

## Filtering Rules

All active filter categories combine with AND. Multiple selected values within
one category combine with OR. My tasks acts as an additional assignment filter
and can coexist with explicit assignee selections. Title search is also an AND
condition.

Due-date choices are mutually exclusive presets: overdue, today, this week,
later, and no due date. Completed tasks remain governed by the helper's existing
Hide completed tasks setting before widget filters run.

Empty buckets stay visible so the board structure does not jump when filters
change. Their empty state distinguishes a filtered-empty bucket from a truly
clear bucket when practical without adding explanatory clutter.

## Refresh And Conflict Behavior

Background refresh must not close dialogs, overwrite unsaved title or checklist
edits, reset filters, or move the user's scroll position. Use generation or
identity checks consistent with the existing detail-loading logic so stale
responses cannot replace data from a newly selected plan.

Before a write, retrieve the latest ETag. If Planner reports a conflict or the
task no longer exists, refresh the affected task or board and show a concise
message. Failed writes retain the user's draft where retrying is safe. A failed
background refresh is non-destructive and leaves the cached board visible.

## Accessibility And Touch

- Use familiar icons for search, filter, edit, delete, and checklist ordering,
  with accessible names and tooltips.
- Keep all interactive targets comfortably touch-sized.
- Do not rely on hover, color alone, or precision dragging.
- Preserve outside-tap closing for panels and dialogs.
- Ensure filter and detail panels scroll independently without locking or
  snapping the board after closure.
- Keep text contained at XENEON EDGE widget sizes.

## Testing

### Helper

- Parse and return new task and plan-label fields.
- Validate task ownership for every write.
- Exercise task and task-details ETag handling and conflict translation.
- Cover title, progress, priority, start date, labels, and all checklist
  mutations, including order hints.
- Verify no new permission scope is requested.
- Persist, isolate, upgrade, and sanitize per-plan display settings.
- Return cached display without a Graph request.

### Widget

- Render card metadata and task detail controls.
- Combine each filter category correctly and preserve Planner ordering.
- Persist structured filters and My tasks per plan while clearing search on
  reload.
- Sanitize deleted members, labels, and buckets.
- Render cached data first and apply a live refresh without losing scroll,
  dialog state, or drafts.
- Exercise new-task defaults and submitted fields.
- Cover checklist add, rename, delete confirmation, completion, and ordering.
- Retain all existing completion, notes, chat, assignment, bucket, date, and
  outside-tap behavior.

### Verification

Run the complete helper and widget test suites, produce a clean release build,
and inspect desktop and XENEON-sized layouts. Final physical touchscreen checks
must include filter scrolling, detail scrolling, checklist ordering, outside-tap
closing, returning to the widget after navigating away, and restoring saved
scroll positions.

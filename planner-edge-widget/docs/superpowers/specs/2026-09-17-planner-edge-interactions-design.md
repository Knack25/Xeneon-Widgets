# Planner Edge Interactions Design

Date: 2026-09-17
Baseline: v0.1.4

## Goal

Let the user switch Planner boards on the XENEON EDGE, open task details without starting completion, and view and complete checklist items with confirmation.

## Interaction Model

- The board title is a button. It opens a compact board picker listing accessible plans, grouped or labeled by Microsoft 365 group. Choosing a plan saves the selection through the helper, closes the picker, and loads that board. A failed save leaves the current board in place and shows an error.
- Each task card has a large, separate checkbox button. Only that button opens the existing task-completion confirmation.
- Tapping the rest of a task card opens a detail view. It shows the full checklist, assignee names, and due date. Missing values are shown plainly as "No checklist," "Unassigned," and "No due date." The detail view can be closed without changing the task.
- A card previews the first three checklist items in Planner order. If more exist, it shows a count of the remainder. Checklist items have their own tap targets; tapping one in the preview or detail view opens a confirmation before marking it complete. Already checked items are displayed but are not sent for completion again.
- Dialogs keep the board visible underneath and have a clear cancel action. A pending write disables duplicate submission; success refreshes the displayed task, while failure leaves the current view open with an actionable message.

## Data Flow

The helper remains the only component that calls Microsoft Graph or holds tokens. It adds task detail data to the display contract, including checklist item ID, title, checked state, and order. The board itself should appear promptly; checklist previews may fill in progressively, with bounded concurrent detail requests and short-lived caching to avoid a burst of Graph calls on every refresh. Failure to fetch one task's details should not blank the board.

The helper exposes focused endpoints for selecting a board, reading fresh task details, and completing one checklist item. A checklist update reads the latest task-details ETag, confirms the item still exists and is unchecked, then PATCHes only that item with `If-Match`. A conflict prompts a refresh rather than overwriting another user's changes. The task-completion flow remains unchanged.

The Planner task's `assignments` property supplies assignee IDs. The helper resolves those IDs to display names using Microsoft Graph basic user profiles, caches successful lookups, and falls back to "Assigned person unavailable" when the directory cannot provide a name. This requires the additional delegated `User.ReadBasic.All` permission, which may need work-administrator approval. The helper should not expose raw IDs as the primary visible label.

Board selection is stored in the existing local settings so the helper setup page and native widget stay in sync. Switching boards invalidates the previous board display cache and does not reuse task details from another plan.

## Scope And Verification

No task creation, title editing, reassignment, due-date editing, or checklist reordering is included. The detail view is for inspection and checklist completion only.

Tests cover board selection and failure rollback, distinct task-card tap targets, three-item preview ordering, detail empty states, checklist confirmation and cancellation, Graph ETag/conflict behavior, and partial detail or directory failures. Verify the packaged native widget on the XENEON EDGE in addition to automated tests.

Microsoft Graph references: [task details](https://learn.microsoft.com/en-us/graph/api/plannertaskdetails-get?view=graph-rest-1.0), [task-details updates](https://learn.microsoft.com/en-us/graph/api/plannertaskdetails-update?view=graph-rest-1.0), [basic user profiles](https://learn.microsoft.com/en-us/graph/permissions-reference#userreadbasicall).

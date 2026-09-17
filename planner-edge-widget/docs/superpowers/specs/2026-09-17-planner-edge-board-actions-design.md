# Planner Edge board actions and task details

## Scope and release order

This design extends the existing native iCUE widget and local helper for a work account. The helper remains the only component that calls Microsoft Graph or holds tokens. Ship the work in three independently testable increments: board ordering and personal filter; task details and edits; task creation. Give each imported widget package a new version and verify it on the XENEON EDGE before release.

## Board order and personal filter

The current helper preserves the order returned by the plan's task-list endpoint. Planner's bucket board instead orders tasks using each task's `bucketTaskBoardFormat.orderHint`. Read and cache that format for visible tasks, sort within each bucket using ordinal hint comparison, and use task ID as a stable tie-breaker. Keep the existing bucket ordering. Fetch formats with bounded concurrency and cache results across the normal display refresh; an unavailable format must leave the board usable and keep that task in a stable fallback position. Compare a known plan with Planner web before settling the final displayed direction. Do not issue an unbounded request for every task at each minute refresh.

Keep each bucket name and task count pinned to the top of its own column while that column's tasks scroll vertically. Give the pinned heading an opaque background and enough stacking priority that task text cannot show through it. Horizontal board scrolling still moves the entire column and its heading together; a short or empty bucket keeps its heading in the normal position. Preserve the current per-bucket scroll position during refreshes and task detail updates.

Add a top-right filter icon with an active state and accessible label, "My tasks". It filters only the selected board, preserving bucket columns and their order, and includes a task when its assignment IDs contain the signed-in user's Microsoft Graph ID. Fetch the user's ID with the existing `User.Read` permission and expose it to the widget through the helper. The filter defaults to off on a new widget load and remains active during refreshes and board switches in the same widget session. The existing hide-completed setting applies before this filter. Show an empty state in a bucket when no tasks remain.

## Task details and editing

Read the task detail `description` and show it below the checklist as plain text with preserved line breaks. Omit the section when empty. This release does not read the Planner comment conversation; comments use a different Microsoft 365 data path.

The Assigned to row opens an in-widget picker with checkboxes for multiple people. Initial selections reflect the task's current assignments. A Save button submits the difference, while closing the picker discards draft changes. An explicit Unassigned state is represented by no selected people. The helper validates that the task is in the selected plan and that newly selected users are eligible for that plan, then patches only changed assignment entries using the task's current ETag. Existing assignees remain visible if member lookup fails. For group-backed plans, list eligible people from the owning Microsoft 365 group. Request the additional delegated `GroupMember.ReadBasic.All` permission through a setup action if the tenant requires it; the existing `User.ReadBasic.All` consent supplies display names. If consent is unavailable, disable editing and explain why in the details view; never present an incomplete roster as the complete member list. Plans without a supported member source can still display assignees but cannot open the edit picker until a reliable roster source is available.

The Due row opens an in-widget month calendar. It shows the current date, supports month navigation, and offers Save, Clear date, and Cancel. A save submits a date only when the user confirms it. Convert the selected local date to the Graph timestamp consistently and verify that the same calendar day appears in Planner web after refresh. The helper uses the current task ETag, rejects a due date before an existing start date with a clear message, and returns conflicts without silently overwriting a newer edit. An empty due date remains a supported state.

## New task

Add a plus icon beside My tasks at the top right. It opens a compact form for title and bucket, with optional due date and assignees using the same calendar and member controls. Title is required; the default bucket is the first real bucket on the selected board. The user can review and cancel before submitting. The helper takes the selected plan ID from its own settings, validates the destination bucket and selected members, creates one task through Graph, and refreshes the board after success. A failed create keeps the form and entered values with a useful error. The submit button is disabled while a create is pending to prevent duplicates. If the personal filter is on and the new task is not assigned to the current user, explain that it was created but is hidden by the filter.

## Interaction and data boundaries

Use buttons, checkboxes, and an in-widget calendar rather than native dropdown or date controls, which have not worked reliably in iCUE. Keep the board mounted while details load so scrolling does not jump. Dialogs close by tapping outside, except while a write is pending. Successful writes invalidate affected task and board caches; unsuccessful writes do not change the displayed task. The helper returns user-facing errors for missing consent, membership lookup failure, Graph throttling, permission denial, and ETag conflicts. No Graph token, user directory, or private note is stored in the widget package.

## Verification

- Compare task order in multiple buckets, including an empty bucket, with the same plan in Planner web. Verify stable order across refreshes and after moving a task.
- Touch-scroll a long bucket vertically and the board horizontally on the XENEON EDGE; verify its name and count remain visible, task cards do not overlap the heading, and opening and closing a dialog does not reset the scroll position.
- Verify My tasks with tasks assigned only to the current user, to multiple users including the current user, and to other users; check board switching and hide-completed interaction.
- Test assignment add, remove, and clear operations, rejected members, denied member-list permission, and concurrent edits.
- Test due-date selection, clearing, month changes, timezone round-trip against Planner web, and start-date validation.
- Test task creation with required and optional fields, duplicate tap prevention, failed creation, and visibility under My tasks.
- Test description rendering for empty and multiline content, including HTML-like text, without executing markup.
- Run helper and widget tests, package validation, and a native touch pass on the XENEON EDGE.

## Microsoft Graph references

- [Planner board order and ETags](https://learn.microsoft.com/en-us/graph/api/resources/planner-overview?view=graph-rest-1.0)
- [Get bucket task board format](https://learn.microsoft.com/en-us/graph/api/plannerbuckettaskboardtaskformat-get?view=graph-rest-1.0)
- [Update a Planner task](https://learn.microsoft.com/en-us/graph/api/plannertask-update?view=graph-rest-1.0)
- [Create a Planner task](https://learn.microsoft.com/en-us/graph/api/planner-post-tasks?view=graph-rest-1.0)
- [Task details](https://learn.microsoft.com/en-us/graph/api/resources/plannertaskdetails?view=graph-rest-1.0)
- [List group members](https://learn.microsoft.com/en-us/graph/api/group-list-members?view=graph-rest-1.0)

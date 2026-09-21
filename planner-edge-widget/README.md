# Planner Edge Widget

Planner Edge Widget is a XENEON EDGE display project for viewing and managing Microsoft Planner tasks from a work account. The board supports title search, My tasks, and filters for assignee, label, priority, bucket, progress, and due-date range. Task cards show high-priority, label, progress, due-date, and checklist context without changing Planner's bucket or task order.

Task details support title, progress, priority, start date, due date, assignee, bucket, and existing-label updates. Checklists can be added, renamed, deleted with confirmation, completed, and moved with touch-friendly up/down controls. Editable notes and Planner task comments remain available, including earlier comment history and first-comment creation. New tasks can include a bucket, assignees, start and due dates, priority, and existing labels.

The project has two parts:

- `../microsoft-widgets-helper`: shared Windows helper for Microsoft sign-in and widget integrations, including Planner.
- `widget`: iCUE widget for the XENEON EDGE that shows and edits tasks on the selected board.

Start with [setup](docs/setup.md). The [design](docs/superpowers/specs/2026-09-16-planner-edge-widget-design.md) and [implementation plan](docs/superpowers/plans/2026-09-16-planner-edge-widget.md) record the architecture.

Structured filters and My tasks are saved independently for each board; title search remains session-only. Horizontal board position and each bucket's vertical position are also restored per board. On startup, the widget displays the helper's saved Planner snapshot immediately, restores that board's view state, and refreshes from Microsoft in the background. A failed refresh leaves the saved board visible as an offline view. Outlook continues to use its existing startup path; equivalent Outlook fast resume is future work.

These Planner organization controls use the existing delegated Microsoft Graph permissions `User.Read` and `Tasks.ReadWrite`; they add no permission or organizational-consent requirement. Task chat additionally requires `Group-Conversation.ReadWrite.All`. Existing users may need to select **Enable task chat** in Microsoft Widgets Setup once. Without that consent, only task chat is disabled: notes continue to read and save through `Tasks.ReadWrite`. Optional `User.ReadBasic.All` and `GroupMember.ReadBasic.All` permissions enable assignee names and assignee editing.

# Planner Edge Widget

Planner Edge Widget is a XENEON EDGE display project for viewing and managing Microsoft Planner tasks from a work account. Task details support checklists, editable notes, and Planner task comments, including earlier comment history and first-comment creation.

The project has two parts:

- `../microsoft-widgets-helper`: shared Windows helper for Microsoft sign-in and widget integrations, including Planner.
- `widget`: iCUE widget for the XENEON EDGE that shows and edits tasks on the selected board.

Start with [setup](docs/setup.md). The [design](docs/superpowers/specs/2026-09-16-planner-edge-widget-design.md) and [implementation plan](docs/superpowers/plans/2026-09-16-planner-edge-widget.md) record the architecture.

Planner requires delegated Microsoft Graph permissions `User.Read` and `Tasks.ReadWrite`; task chat additionally requires `Group-Conversation.ReadWrite.All`. Existing users may need to select **Enable task chat** in Microsoft Widgets Setup once. Without that consent, only task chat is disabled: notes continue to read and save through `Tasks.ReadWrite`. Optional `User.ReadBasic.All` and `GroupMember.ReadBasic.All` permissions enable assignee names and assignee editing.

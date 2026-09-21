# Planner Notes and Task Chat Design

## Goal

Extend the Planner task detail view so users can edit task notes and read or post the same comments shown on the task in Microsoft Planner. Notes and chat must fail independently so that missing conversation permission never disables normal Planner functionality.

## Scope

This release includes:

- Editing the existing Planner task description through an explicit **Save notes** action.
- Reading the Microsoft 365 group conversation attached to a Planner task.
- Posting a new comment to an existing task conversation.
- Creating and attaching a conversation when the first comment is posted to a task that does not yet have one.
- Graceful behavior when conversation permission has not been granted.
- Recent-first pagination support presented as an oldest-to-newest chat timeline with a **Load earlier comments** action.

This release does not include editing or deleting existing comments, rich-text composition, reactions, attachments, mentions, or real-time push updates.

## Authorization

The Planner connection requests these capabilities together:

- `User.Read`
- `Tasks.ReadWrite`
- `Group-Conversation.ReadWrite.All`

Existing optional Planner capabilities remain unchanged. The helper detects whether conversation access is actually available after sign-in. If it is missing or denied, task boards, task details, checklist actions, and editable notes continue to work. Only the chat section displays an approval or unavailable message.

Existing installations may need to reconnect once after the application registration and tenant consent are updated.

## Microsoft Graph Data Flow

### Task and plan identity

The helper already receives the selected plan's Microsoft 365 group ID. Extend the local `GraphTask` model and Planner task parsing to retain `conversationThreadId` from Microsoft Graph.

The group ID and conversation thread ID identify the task's comments without searching all group conversations.

### Notes

1. Read notes from `plannerTaskDetails.description`, as the helper does today.
2. Accept a notes update through `PUT /api/planner/tasks/{taskId}/notes`.
3. Reload the current task details to obtain the latest details ETag.
4. Patch `/planner/tasks/{taskId}/details` with the new `description` and `If-Match` header.
5. Invalidate cached task details only after a successful update.

Notes remain plain text. Line breaks are preserved.

### Reading chat

1. Accept `GET /api/planner/tasks/{taskId}/chat` with an optional continuation token.
2. Load the current task and selected plan context.
3. If the task has no `conversationThreadId`, return an empty available conversation.
4. Otherwise request `/groups/{groupId}/threads/{threadId}/posts` and map each post to a small local contract containing ID, author, timestamp, plain-text body, and continuation information.
5. Return a capability-specific unavailable response for missing consent instead of converting the entire task detail request into an error.

Chat is loaded only while task details are open. It is not part of board refreshes.

### Posting chat

Expose `POST /api/planner/tasks/{taskId}/chat` with a non-empty plain-text message.

For an existing conversation, reply to the task's group conversation thread. After Microsoft accepts the reply, reload the visible chat page so the server remains the source of truth.

For a task without a conversation:

1. Create a new thread in the plan's Microsoft 365 group with the submitted message as its first post.
2. Patch the Planner task's `conversationThreadId` using the task's current ETag.
3. Invalidate task and detail caches.
4. Return the refreshed conversation.

If thread creation succeeds but attaching it to the task fails, report the failure clearly and do not silently retry in a way that could create duplicate conversations.

## Detail View

The existing task detail dialog keeps its metadata controls and checklist. Below those sections it shows Notes followed by Task chat.

### Notes section

- A multiline editor is populated with the current description.
- An explicit **Save notes** button performs the update.
- The button is disabled while saving.
- Saved and error states appear beside the control.
- Failed requests preserve the user's text.

### Task chat section

- Messages appear oldest-to-newest.
- Each message shows the author, a timestamp in the PC's configured local time, and message text.
- A **Load earlier comments** control appears when more history is available.
- An empty conversation displays `No comments yet.`
- A multiline composer and **Post comment** button sit below the history.
- Posting is disabled while a request is in progress.
- A failed post preserves the drafted comment.
- Existing comments cannot be edited or deleted.

When conversation access is unavailable, replace the history and composer with a compact explanation that administrator approval is required. The Notes section and all existing task actions remain usable.

The existing backdrop tap behavior continues to close the dialog. Dialog scrolling and body scroll restoration must continue working on the XENEON EDGE touchscreen.

## Content Safety

Microsoft group conversation posts can contain HTML. The helper converts supported message structure to plain text with preserved line breaks, and the widget renders it through text-only DOM APIs. Raw conversation HTML is never inserted into the page.

User-entered notes and comments are length-checked and treated as plain text. Error responses do not include access tokens or raw authorization details.

## Conflict and Failure Handling

- Notes and first-thread attachment use current ETags.
- A `412 Precondition Failed` reloads current task state and returns a conflict message asking the user to retry; it never overwrites newer Planner data automatically.
- Notes and chat have independent loading and error states.
- Permission failures are distinguished from temporary Microsoft Graph or network failures.
- The UI prevents duplicate submissions but does not optimistically add comments before Graph accepts them.
- Cancellation from closing the dialog must not surface as an error notification.

## Verification

Automated helper tests cover:

- Reading and updating notes with the details ETag.
- Cache invalidation after a successful notes update.
- Reading an existing task conversation and mapping author, timestamp, and safe text.
- Empty chat for a task without a thread.
- Posting a reply to an existing thread.
- Creating the first thread and attaching it to the Planner task.
- Missing conversation permission fallback.
- ETag conflicts, Graph failures, pagination, empty input, and HTML conversion.

Widget tests cover:

- Notes editing, explicit save, saved state, and retained text after failure.
- Chat loading, chronological display, pagination, posting, and retained drafts after failure.
- Empty and unavailable chat states.
- Safe rendering of HTML-like content.
- Touch scrolling, backdrop dismissal, and scroll restoration after closing details.

Manual verification uses both a task with existing comments and a task without a conversation on the XENEON EDGE display.

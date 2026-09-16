# Planner Edge Widget

Planner Edge Widget is a XENEON EDGE display project for showing and completing Microsoft Planner tasks from a work account.

The planned architecture uses two parts:

- A local Windows helper service that handles Microsoft sign-in, Microsoft Graph calls, caching, and task completion.
- A packaged iCUE widget that runs on the XENEON EDGE and talks only to the local helper over localhost.

See `docs/superpowers/specs/2026-09-16-planner-edge-widget-design.md` for the current design.

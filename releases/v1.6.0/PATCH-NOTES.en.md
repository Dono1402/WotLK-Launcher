# Atlas Launcher 1.6.0

Atlas Launcher 1.6.0 improves launcher responsiveness, the reliability of Messages and addons, and the checks applied to sessions, downloads, installations, and updates.

Published: 2026-09-10T13:20:21Z

## Friends

- More compact list with a clearer distinction between Atlas account names and characters.
- Quieter offline information and a presence badge anchored to the avatar.
- Quick messaging action on friend cards.
- Search by account or character name, with collapsible Online/Offline groups.
- Less frequent refreshing while the launcher is in the background, with an immediate update when it returns to the foreground.
- Clearer “Remove this friend” action in the friends menu.

## Messages

- Main text increased to 15 px and secondary information to 12 px.
- Improved contrast while preserving the Citadel backdrop.
- Ctrl+F conversation search with highlighted results and history navigation.
- Rearranged compact video preview controls to keep timestamps readable at 12 px.
- Audio and video files near the visible area are prepared earlier, including on hover and keyboard focus, so playback starts faster.
- Timeline navigation remains stable during dragging and avoids unnecessary media repositioning.
- A Retry button reopens Messages after an error, restoring the conversation and its previously saved draft without restarting the launcher.
- Periodic draft checkpoints during continuous typing, with automatic retries and a discreet error message if saving fails.
- Messaging recovers from temporary local read failures without restarting or discarding drafts and pending messages.
- The Retry button remains available after temporarily switching to fallback mode.

## Addons

- More compact catalogue, with 15 px names and 12 px secondary information.
- Category, favourite and manual-installation filters, sorting and Ctrl+F search.
- Multi-addon selection, packs and local profiles, with selection import and export.
- Automatic dependency installation with a preview and confirmation before replacing manual installations.
- Stable batch progress, persistent cancellation and retries after a failure or interruption.
- External addon inventory, file verification and managed-package reinstallation.
- Separate labels for declared compatibility and documented Atlas validation.
- Catalogue scroll position and the selected addon are preserved after navigation and list refreshes.
- Clearer actions labelled “Uninstall” and “Deselect all”.
- The catalogue, archives and local addon state are checked more strictly before any changes are made.
- Grouped installations, updates and repairs are fully prepared before they are applied; previous folders are restored if any step fails or is cancelled.

## Profile and armory

- More compact presence menu: clicking the current status expands the four choices, which collapse again after selection.
- The armory can be reopened after an error while preserving the requested shared character.
- Cached profile maintenance and armory shutdown run in the background to keep the launcher responsive.
- Reduced memory allocations when switching between profiles, with the previous profile hidden immediately while the next one loads.

## Navigation and responsiveness

- Scroll position is preserved when returning to pages, while temporary menus and confirmations close.
- Lighter rendering of long friends and release-note lists; only items near the visible area are prepared.
- Bounded avatar cache memory and improved release of old interface elements after navigation or language changes.
- Avatars remain visible when their disk cache is unavailable; writes and cleanup are batched in the background.
- Pages use less temporary memory when opening.
- A more compact Release notes page, with headings, text and spacing aligned with the other pages.
- Starting the launcher directly now opens its current interface without requiring an additional launch option.
- Unnecessary waiting messages and confirmations have been removed from several pages, while useful errors and progress remain visible.

## Settings

- More robust settings saves prevent incomplete files if writing is interrupted.
- Settings can be recovered from a backup when the main file is missing or unreadable, with a message asking you to check your preferences.
- Preferences save off the interface thread, restoring the previous value on failure and waiting for an ongoing write during shutdown.
- Changing the game language keeps the interface responsive while updating the game configuration, with a message if the file cannot be modified.

## Security and compatibility

- Sign-in, session renewal, password changes and sign-out remain consistent when operations overlap or are interrupted.
- Unexpected or abnormally large Atlas responses are rejected without replacing or clearing the active session.
- Signing out and switching accounts ignore delayed network responses so an old session cannot become active again.
- Password changes remain compatible with the Atlas services currently deployed without unnecessarily interrupting the active session.
- Sign-in tokens are attached only to HTTPS requests intended for Atlas services.
- Recognised historical Atlas addresses in catalogues and manifests are replaced with an HTTPS address on the official domain before any request. Other insecure or unexpected origins remain rejected.

## Installation and updates

- Game manifests and downloads are checked more strictly before the installation folder is modified.
- Game installation, repair and removal validate the target folder more thoroughly, reject ambiguous locations and cleanly cancel incomplete operations.
- The Atlas Launcher installer stages and verifies its files before activation, then removes incomplete changes if installation fails. The launcher is subsequently started without administrator privileges.
- Launcher updates verify a signed manifest and its package before applying them. Replacement can restore the previous installation if it fails or if the updated launcher does not start correctly.
- Uninstalling Atlas Launcher targets only its registered components, shortcuts and Windows entry. The game, addons, WoW configuration and the user’s Atlas data are preserved.

## Windows

- Fixed the notification icon’s right-click menu sometimes appearing behind the Windows overflow panel.


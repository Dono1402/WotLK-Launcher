# Atlas Launcher 1.4.0

A redesigned interface, improved game launch checks, richer friend presence, and account and addon improvements. The profile and 3D armory join the public client being prepared for version 1.4.0.

Published: 2026-09-06T18:31:16Z

## Launcher

- The Game, Addons, Release notes and Settings pages have been redesigned around an Icecrown Citadel backdrop, panoramic Lich King artwork and new blue surfaces.
- A consistent new typeface, higher-contrast secondary text and improved text rendering across launcher pages, menus and dialogs.
- A cleaner navigation bar, an icon for opening friends, and the launcher version grouped with settings.
- The window now has a fixed size and can be dragged using the navigation bar or the empty top margin.
- Sign-in dialogs, the profile menu, friends, image cropping and the activity center now share the same design. Overlays properly cover the content behind them.
- Redundant subtitles, focus boxes and action tooltips have been removed. Details for truncated text and reasons why actions are unavailable remain accessible.

## Game

- Server status is displayed above the Play button and refreshed automatically.
- The Play button indicates when the game is launching or running, and becomes available again when the game closes.
- When the server is confirmed offline, the button shows Server unavailable and prevents launching. Play becomes available again when the server returns online; maintenance tools remain accessible.
- Download and verification progress labels have separate space to prevent overlapping.
- Improved handling of Windows permissions when the game folder is not writable.

## Profile and account

- Manage my profile and Manage my account are separate, distinguishing the public profile from account settings.
- A bio and personal status can be displayed on the profile viewed by friends.
- Profile pictures up to 25 MB are supported. Clicking the avatar lets you choose a picture, then crop it by repositioning it and zooming with the mouse wheel.
- Fixed consistency between cropping and preview, avatar updates after saving, and synchronization after signing in again.
- Security and Sessions pages, input fields and saving states have been reorganized to make actions clearer.
- Verification status is shown next to the email address. A warning appears only when confirmation is required, and the new-address field starts empty.
- Signing in again from the same device no longer creates duplicate sessions. Signing out returns to the sign-in screen without leaving the account or game page visible behind it.

## Friends and presence

- The friends list has been reorganized with search, friend requests and clearer presence information.
- Presence now distinguishes Connected to launcher from In game. A friend can appear online even when none of their characters are in the game.
- The online count, online-friend ordering and last-seen information now account for launcher activity.
- Clicking a friend opens their picture, display name, status, bio and characters, including class, level, zone and last-seen information.
- The featured character is no longer duplicated among other characters. When offline, it is labeled Last character played; class icons and colors improve readability.
- New friend requests trigger notifications. A sound notification, which can be disabled in settings, announces friend connections; moving from the launcher into the game does not trigger a second notification.
- Removing a friend now requires confirmation, with improved menus, selection, return to the list and dismissal with Escape.

## Addons

- A more compact catalog, single-line descriptions and shortened versions. Full information remains available in details and tooltips.
- Search now preserves spaces while typing, including multiword names such as Deadly Boss.
- Refreshing an addon details panel no longer moves focus to its close button.
- The removal confirmation keeps keyboard focus inside its dialog. English translations for filter and catalog-update labels have also been completed.

## Settings and Windows

- The interface is available in French and English, with no restart required when switching languages.
- Settings have been reorganized, repeated descriptions removed, and rounded toggles now have suitable animations and cursors.
- Start with Windows now launches the launcher minimized to the taskbar without taking focus. The previous startup setting is migrated automatically.
- Only one launcher instance opens at a time. A second automatic startup does not bring the existing window to the foreground.
- The launcher remains accessible in the Windows notification area. Depending on the selected setting, closing the window can move it there and remove its taskbar button.

## Release notes and updates

- The Release notes tab has a dedicated page, with changes grouped by category and previous versions retained.
- Release notes are easier to read, with larger text, shorter lines and more line spacing.
- A green button appears in the top bar when a launcher update is available. It starts installation and opens download progress.
- The version and update status share one line in settings. Not checked, checking, up to date, update available and error states are distinguished.

## Interaction fixes

- On the authentication screen, Enter respects the active field or button and no longer submits sign-in from the registration tab.
- Clicking outside the profile menu keeps focus on the selected control. In the main interface, Tab moves through input fields without visiting buttons, toggles or addon rows.
- Account forms and cropping protect changes while they are being saved. Errors, progress and confirmation buttons remain visible during saving.

## Profile and 3D armory

- The immersive profile and 3D armory are integrated into the public client, accessible from Manage my profile.
- Browse account characters, search them and select which character to display.
- An animated 3D preview reflects the character’s appearance and equipment, with rotation, zoom, recentering, animation pause and weapon display controls.
- Equipment is displayed by slot, with item tooltips in French and English. Statistics use the latest available server snapshot and indicate missing values.
- Avatar, bio and status editing are grouped in the profile. The picture, name and profile text are larger.
- A custom banner is saved locally for each account, with import, replacement, reset and preview before saving.
- Crop the banner by moving the image, using the slider or zooming with the mouse wheel from 100 to 300%. Canceling preserves the saved banner.
- The navigation bar appears when hovering anywhere over the banner, including the avatar, and starts hiding as soon as the pointer leaves. It remains accessible while its menus are in use.
- Opening and closing profile editing preserves the 3D camera’s zoom and orientation. A rejected image-picker request no longer leaves buttons stuck.

## Installation and distribution

- The launcher includes the components required by the profile and armory, without manually installing additional tools.
- Characters and their equipment are loaded for the signed-in account. The armory provides access only to characters belonging to that account.
- If Microsoft WebView2 is missing or too old, the launcher automatically installs the version required to open the profile.

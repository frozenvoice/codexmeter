# Manual validation

ProMeter reconstructs usage from ChatGPT account history. These checks require a real signed-in Pro account. Live ChatGPT compatibility is not claimed until they pass.

## Companion pairing

1. Load the unpacked `extension/` in Chrome or Edge.
2. Register the Chrome/Edge native host from Settings with that extension ID.
3. Sign in to ChatGPT in that normal browser. Do not use WebView2 for Google/Microsoft/Apple.
4. Click **Connect to ProMeter** in the extension popup.
5. Run the first manual sync. Sign-in alone must not scan history.

## Multi-device

1. On this PC, open the tray flyout and note the GPT Pro count.
2. On the Android ChatGPT app, send one GPT-5.6 Sol Pro or GPT-6 Pro message.
3. On this PC, choose **Sync now**.
4. Confirm the Pro counter increases by 1.

## Another PC

1. Use ChatGPT on a second Windows PC with the same account.
2. Sync on the machine running ProMeter.
3. Confirm the remote usage appears.

## Reasoning

1. In ChatGPT, send one GPT-5.6 Sol message with Extra High (`매우 높음`).
2. Sync.
3. Confirm **SOL REASONING → Extra High** increases by 1.
4. Confirm the Pro meter does **not** increase for non-Pro Sol reasoning.

## Regenerate

1. Send one Pro message.
2. Regenerate the response.
3. Sync.
4. If the conversation endpoint returns every branch `request_id`, the Pro count should increase by 1 for the regenerate.
5. If only the current branch is returned, Coverage should stay Estimated/Good and this limitation should remain visible.

## Archive

1. Use Pro in a chat, then archive that chat.
2. Sync.
3. Confirm the current-period Pro count still includes that usage.

## Project

1. Send a Pro message inside a ChatGPT Project.
2. Sync.
3. Confirm the event appears and Coverage lists Projects as available.

## Temporary / deleted

1. Use Temporary Chat or delete a conversation.
2. Confirm those turns do **not** appear after sync.
3. Coverage should continue to list Temporary and Deleted as unavailable.

## Official export

1. From ChatGPT Settings → Data Controls, download `conversations.json` if available.
2. Import it from ProMeter Settings.
3. Import the same file again.
4. Confirm usage counts do not double.

## Theme readability (Dark and Light)

Check each screen in Dark, then repeat in Light (Settings → Theme):

- Main
- Settings
- Flyout
- Coverage
- Welcome
- About

Confirm labels, text boxes, combo boxes and dropdown items, checkboxes, tab headers, DataGrid text/headers, and flyout values stay readable. No black/default text on a dark background, and no washed-out light text on a light background.

## Browser companion with VPN left on

Do not disable the browser VPN or proxy, switch browsers, or weaken browser security.

1. Keep the VPN/proxy enabled in Edge or Chrome.
2. Confirm chatgpt.com itself loads and is signed in in a normal tab.
3. Confirm the companion popup shows connected to ProMeter.
4. Leave that ChatGPT tab open (or let ProMeter open https://chatgpt.com/ and sign in there, then retry).
5. Run a manual sync.
6. Confirm ProMeter does **not** tell you to disable VPN.
7. If ChatGPT returns 403, the UI/log should say `ChatGPT rejected the page request (403)`, not `ChatGPT session expired`, unless the ChatGPT tab is independently signed out.
8. If no ChatGPT tab exists, the status should be `Open/sign in to ChatGPT, then retry`.
9. If MAIN-world page execution is blocked, the status should be `ChatGPT page bridge unavailable` rather than a silent service-worker fetch.

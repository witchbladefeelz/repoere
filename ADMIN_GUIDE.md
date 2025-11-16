# Bot Usage Guide

## Architecture

- `bot.py` — main entry point; launching it automatically starts both the customer bot and the admin bot.
- `admin_bot.py` — contains the admin bot handlers and menu logic (imported by `bot.py`, no need to run separately).

### How to start

```bash
python bot.py
```

The admin bot displays an inline menu:
- Press a button for instant actions (statistics, user list).
- For actions requiring parameters (ban/unban, add days, lookup, delete HWID) the bot will prompt you to enter the details.
- Send `cancel` or `stop` to abort the current action.

## Administrator setup

Add your Telegram ID to the admin list in `bot.py`:

```python
ADMIN_IDS = [8123918703]  # Replace with your actual Telegram IDs
```

Use @userinfobot in Telegram to find your ID.

## Customer commands

- `/start` — show the main menu
- `/purchase <days>` — issue a one-time subscription key for the chosen duration
- `/mykeys` — list unused keys
- `/profile` — view profile with HWIDs, expiry data, and reset buttons

## Admin commands

### Analytics
- `/stats` — system-wide statistics (users, keys, subscriptions)

### User management
- `/ban <user_id>`
- `/ban hwid <hwid>`
- `/unban <user_id>`
- `/unban hwid <hwid>`
- `/user <user_id>`
- `/user hwid <hwid>`
- `/users` — list recent users/key holders (first 25 entries per category)

### Subscription management
- `/adddays <user_id> <days>`
- `/adddays hwid <hwid> <days>`
- `/deletehwid <hwid>` — permanently delete the HWID entry

## HWID reset requests

- Users with an active subscription can submit an HWID reset from `/profile`.
- When a request is sent, all admins receive a notification from the admin bot.
- Review the request and remove the HWID via `/deletehwid <hwid>`; issue a new key if needed.

## Typical admin workflow

```
/stats
/ban 123456789
/unban hwid ABC123XYZ
/user 123456789
/adddays 123456789 30
```

## Profile information (customer bot)

- Telegram ID
- Number of active keys
- Total purchased days
- Registered HWIDs with:
  - Expiration date
  - Active / expired status
  - Ban status
  - Remaining days and hours

## Notifications

When a key is activated through `activate-license.php`, the customer bot notifies the user with:
- Activated key
- HWID
- Expiration date
- Status (new subscription or extension)


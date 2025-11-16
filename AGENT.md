# AGENT BRIEF

## Overview
- `src/HwidBots.MultiBot` – single .NET 8 console project that launches **user** and **admin** Telegram bots together.
- Bots interact with MariaDB/MySQL using shared repository/service layer.
- PHP API (`activate-license.php`, `check-license.php`) is packaged with a Docker image together with MariaDB (see root `Dockerfile` + `start.sh` + `mysql.sql`).  
- Legacy Python/other files have been superseded; everything now lives inside `HwidBots.MultiBot` plus the Docker PHP stack.

## Project Layout (`src/HwidBots.MultiBot`)
- `Program.cs` – Host builder; registers config, logging, services, hosted services.
- `appsettings.json` – runtime configuration. **Update `"Database"` section** to point to the desired DB instance (for Docker use `localhost:3307`, user `hwid`, password `hwidpass`). Admin IDs & bot tokens also live here.
- `GlobalUsings.cs` – shared imports.
- `Options/` – POCO options classes (`BotDatabaseOptions`, `BotCommonOptions`, `UserBotOptions`, `AdminBotOptions`). 
- `Models/` – DTOs/records used by repository layer (all numeric fields are `long` to match MySQL int/bigint).
- `Services/DatabaseService.cs` – thin wrapper around MySqlConnector/Dapper (`AllowPublicKeyRetrieval=true` already set).
- `Services/LicenseRepository.cs` – encapsulates all SQL queries & business operations (keys, stats, bans, resets, etc.).
- `UserBot/` – `UserSessionStore`, `UserBotUpdateHandler`, `UserBotHostedService` handle user interactions, purchase flow, stats, reset buttons.
- `AdminBot/` – `AdminSessionStore`, `AdminBotUpdateHandler`, `AdminBotHostedService` provide admin menu, stats, CRUD operations.

## Running the Bots
1. Ensure the database is reachable:
   - For local Docker setup use provided image (see below) or your own MySQL instance.
   - Update `appsettings.json` accordingly (`Host`, `Port`, `User`, `Password`, `Database`).
2. Run:
   ```powershell
   dotnet run --project src\HwidBots.MultiBot
   ```
   Logs will show user/admin bot start-up messages. Keep the window open; stop with `Ctrl+C`.
3. Common build issue: `HwidBots.MultiBot.exe` locked by previous run. Stop lingering process first:
   ```powershell
   taskkill /IM HwidBots.MultiBot.exe /F
   ```

## Docker PHP + MySQL Stack
Files at repo root:
- `Dockerfile`, `start.sh`, `mysql.sql`, `php/` directory (contains `config.php`, `activate-license.php`, `check-license.php`).
- Image boots MariaDB, applies `mysql.sql`, and runs PHP built-in server on port `8080`.
- Default credentials (override via `docker run -e ...`):
  - `MYSQL_ROOT_PASSWORD=your_root_password`
  - `MYSQL_DATABASE=syntara`
  - `MYSQL_USER=hwid`
  - `MYSQL_PASSWORD=hwidpass`
  - `BOT_TOKEN=<admin bot token>`
- Run container (note host port 3307 to avoid conflicts with local MySQL):
  ```powershell
  docker build -t hwid-app .
  docker run -d --name hwid-app `
    -e MYSQL_ROOT_PASSWORD=your_root_password `
    -e MYSQL_DATABASE=syntara `
    -e MYSQL_USER=hwid `
    -e MYSQL_PASSWORD=hwidpass `
    -e BOT_TOKEN=<admin bot token> `
    -p 8080:8080 -p 3307:3306 hwid-app
  ```
  API endpoints:
  - `http://localhost:8080/activate-license.php`
  - `http://localhost:8080/check-license.php`
  Database accessible on `localhost:3307` (user `hwid`, password `hwidpass`, DB `syntara`).

## Troubleshooting
- **`Access denied for user`** – check `appsettings.json` vs. DB credentials (bots were defaulting to `root`; update to `hwid/hwidpass`).
- **`A parameterless default constructor…` from Dapper** – all record types now use `long`; ensure models match DB column types.
- **`File ... locked by HwidBots.MultiBot`** – terminate running process before rebuilding/rerunning (`taskkill /IM HwidBots.MultiBot.exe /F`).
- **Port conflicts with Docker** – if port 3306 already used, map container DB to another host port (`-p 3307:3306`).

## Next Steps / Maintenance
- Consider adding persistence (Docker volume) for MariaDB if container restarts regularly.
- Optional: suppress nullable warnings (`UserSessionStore`, `DatabaseService`) or refactor to avoid them.
- Ensure tokens/secrets are not committed; eventually migrate to environment variables / user secrets.

Happy hacking! ✨


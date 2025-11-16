# HWID Subscription Bot

Telegram bot system for managing HWID-based subscriptions with payment processing and key management.

## Features

- 🔑 Subscription key generation and management
- 💰 Payment request system with admin approval
- 👥 Multi-admin support
- 📦 Automatic software updates distribution
- 🔐 Password-protected archives
- 💳 Multiple cryptocurrency payment methods
- 📊 Admin statistics and logging
- 🌐 Group and private chat support

## Tech Stack

- .NET 8.0
- MariaDB (via Docker)
- Telegram.Bot API
- Dapper ORM
- DotNetZip

## Prerequisites

- .NET 8.0 SDK
- Docker (for MariaDB)
- Two Telegram bots (User bot and Admin bot)

## Setup

### 1. Clone the repository

```bash
git clone <your-repo-url>
cd hwid
```

### 2. Configure the application

Copy the example configuration and edit it:

```bash
cp src/HwidBots.MultiBot/appsettings.example.json src/HwidBots.MultiBot/appsettings.json
```

Edit `appsettings.json` and configure:
- Database connection
- Bot tokens (get from [@BotFather](https://t.me/BotFather))
- Admin IDs
- Admin group ID
- Payment details
- Support contact
- Prices

### 3. Start MariaDB

```bash
chmod +x start.sh
docker build -t hwid-mariadb .
docker run -d --name hwid-db -p 3307:3306 hwid-mariadb
```

### 4. Build and run

```bash
dotnet build
dotnet run --project src/HwidBots.MultiBot
```

## Configuration

### appsettings.json

```json
{
  "Database": {
    "Host": "localhost",
    "Port": 3307,
    "Database": "syntara",
    "User": "hwid",
    "Password": "hwidpass"
  },
  "Bot": {
    "AdminIds": [123456789],
    "AdminGroupId": -1001234567890,
    "SupportContact": "@your_support",
    "PaymentDetails": "Your payment details here",
    "Prices": {
      "30": 45.00,
      "90": 100.00,
      "180": 150.00,
      "99999": 300.00
    }
  },
  "UserBot": {
    "BotToken": "YOUR_USER_BOT_TOKEN"
  },
  "AdminBot": {
    "BotToken": "YOUR_ADMIN_BOT_TOKEN"
  }
}
```

## User Bot Commands

- `/start` - Start the bot and show main menu
- `/profile` - View subscription status
- `/mykeys` - View active keys
- `/purchase` - Purchase subscription

## Admin Bot Commands

- `/admin` - Show admin panel
- `/stats` - View statistics
- `/logs` - View action logs
- `/create_key <user_id> <days>` - Create subscription key
- `/ban <hwid>` - Ban HWID
- `/unban <hwid>` - Unban HWID
- `/reset <hwid>` - Reset HWID

## Deployment

### Linux Server

1. Install .NET 8 SDK:
```bash
wget https://dot.net/v1/dotnet-install.sh
chmod +x dotnet-install.sh
./dotnet-install.sh --channel 8.0
```

2. Install Docker:
```bash
curl -fsSL https://get.docker.com -o get-docker.sh
sudo sh get-docker.sh
```

3. Deploy the application:
```bash
dotnet publish src/HwidBots.MultiBot -c Release -o /opt/hwid-bot
```

4. Create systemd service:
```bash
sudo nano /etc/systemd/system/hwid-bot.service
```

5. Start the service:
```bash
sudo systemctl enable hwid-bot
sudo systemctl start hwid-bot
```

## License

Private project - All rights reserved

## Support

For support, contact: @sentanilsupport
